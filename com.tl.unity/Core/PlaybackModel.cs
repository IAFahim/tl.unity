using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

[Flags]
public enum PlaybackFlags : byte
{
    None = 0,
    Reverse = 1 << 0,
    Paused = 1 << 1,
    StartPending = 1 << 2,
    Ended = 1 << 3,
    Stopping = 1 << 4,
    StopAged = 1 << 5,
    Began = 1 << 6,
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct Playback
{
    public ushort Timeline;
    public ushort Tick;
    public byte Channel;
    public PlaybackFlags Flags;
    public ushort Reserved;
}

public readonly struct EntityRef
{
    public EntityRef(int index, int version)
    {
        Index = index;
        Version = version;
    }

    public readonly int Index;
    public readonly int Version;
}

public readonly unsafe struct PlaybackView
{
    internal PlaybackView(Playback* slots, int* counts, int entities, int slotsPerEntity)
    {
        Slots = slots;
        Counts = counts;
        EntityCount = entities;
        SlotsPerEntity = slotsPerEntity;
    }
    public readonly Playback* Slots;
    public readonly int* Counts;
    public readonly int EntityCount;
    public readonly int SlotsPerEntity;

    public int CountOf(int entity) => Counts[entity];
    public Playback* SlotOf(int entity, int slot) => Slots + (nuint)entity * (nuint)SlotsPerEntity + (nuint)slot;
}

public sealed unsafe class PlaybackTable : IDisposable
{
    Playback* _slots;
    readonly int* _counts;
    readonly int _entities;
    int _slotsPerEntity;
    readonly ulong* _playing;
    readonly int _playingWords;
    KindStore? _kinds;
    readonly RequestQueue* _consumer;
    readonly RequestQueue* _pending;
    readonly ClosingList* _closing;
    bool _disposed;

    public PlaybackTable(int entityCapacity, int slotsPerEntity = 4)
    {
        if (entityCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        if (slotsPerEntity <= 0 || slotsPerEntity > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slotsPerEntity));
        _entities = entityCapacity;
        _slotsPerEntity = slotsPerEntity;
        var slots = NativeMemory.AlignedAlloc((nuint)entityCapacity * (nuint)slotsPerEntity * 8u, 8u);
        var counts = NativeMemory.AlignedAlloc((nuint)entityCapacity * 4u, 8u);
        NativeMemory.Clear(slots, (nuint)entityCapacity * (nuint)slotsPerEntity * 8u);
        NativeMemory.Clear(counts, (nuint)entityCapacity * 4u);
        _slots = (Playback*)slots;
        _counts = (int*)counts;
        _playingWords = (entityCapacity + 63) >> 6;
        _playing = (ulong*)NativeMemory.AlignedAlloc((nuint)_playingWords * 8u, 8u);
        NativeMemory.Clear(_playing, (nuint)_playingWords * 8u);
        _consumer = RequestQueue.Create();
        _pending = RequestQueue.Create();
        _closing = ClosingList.Create();
    }

    public const int MaxSlots = 256;

    public int EntityCapacity => _entities;
    public KindStore? Kinds => _kinds;
    internal ulong* Playing => _playing;
    internal int PlayingWords => _playingWords;

    internal void SetPlaying(int entity, bool value)
    {
        if (value) _playing[entity >> 6] |= 1ul << (entity & 63);
        else _playing[entity >> 6] &= ~(1ul << (entity & 63));
    }

    internal void AttachKinds(KindStore? kinds)
    {
        if (kinds != null && _kinds != null) throw new InvalidOperationException("A KindStore is already attached to this PlaybackTable.");
        _kinds = kinds;
    }
    public int SlotsPerEntity => _slotsPerEntity;
    public PlaybackView View => new(_slots, _counts, _entities, _slotsPerEntity);
    public RequestQueue* Pending => _pending;
    public RequestQueue* Consumer => _consumer;
    public ClosingList* Closing => _closing;

    public int CountOf(int entity) => _counts[entity];

    public bool IsPlaying(int entity, ushort timeline)
    {
        var slots = new Slots(this, entity);
        return PlaybackRules.IsPlaying(ref slots, timeline);
    }

    void Grow()
    {
        var stride = Math.Min(MaxSlots, _slotsPerEntity * 2);
        var bytes = (nuint)_entities * (nuint)stride * 8u;
        var slots = (Playback*)NativeMemory.AlignedAlloc(bytes, 8u);
        NativeMemory.Clear(slots, bytes);
        for (var e = 0; e < _entities; e++)
            new ReadOnlySpan<Playback>(_slots + (nuint)e * (nuint)_slotsPerEntity, _counts[e]).CopyTo(new Span<Playback>(slots + (nuint)e * (nuint)stride, stride));
        NativeMemory.AlignedFree(_slots);
        _slots = slots;
        _slotsPerEntity = stride;
    }

    internal void CloseSlot(int entity, int slot)
    {
        var slots = _slots + (nuint)entity * (nuint)_slotsPerEntity;
        ClosingList.Add(_closing, entity, slot, in slots[slot]);
        if (_kinds == null) RemoveSlot(entity, slot);
        else _kinds.Close(entity, slot, ref slots[slot]);
    }

    internal void RemoveSlot(int entity, int slot)
    {
        var slots = _slots + (nuint)entity * (nuint)_slotsPerEntity;
        slots[slot] = default;
        var count = _counts[entity];
        while (count > 0 && slots[count - 1].Timeline == 0) count--;
        _counts[entity] = count;
    }

    internal Playback* SlotsOf(int entity) => _slots + (nuint)entity * (nuint)_slotsPerEntity;
    internal int* Counts => _counts;

    public bool Find(int entity, byte channel, out Playback playback)
    {
        var slots = new Slots(this, entity);
        return PlaybackRules.Find(ref slots, channel, out playback);
    }

    internal struct Slots : IPlaybackSlots
    {
        readonly PlaybackTable _table;
        readonly int _entity;
        Playback* _items;

        public Slots(PlaybackTable table, int entity)
        {
            _table = table;
            _entity = entity;
            _items = table.SlotsOf(entity);
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _table._counts[_entity];
        }

        public ref Playback this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _items[index];
        }

        public bool StartsPending => _table._kinds != null;

        public bool TryAdd(in Playback playback)
        {
            var count = _table._counts[_entity];
            if (count == _table._slotsPerEntity)
            {
                if (count == MaxSlots) return false;
                _table.Grow();
                _items = _table.SlotsOf(_entity);
            }
            _items[count] = playback;
            _table._counts[_entity] = count + 1;
            return true;
        }

        public bool Admit(ushort timeline)
        {
            _table._kinds?.ValidateStart(_entity, timeline);
            return true;
        }

        public void Close(int index) => _table.CloseSlot(_entity, index);
    }

    public void Start(int entity, ushort timeline, ushort at = 0) => RequestQueue.Add(_pending, entity, -1, timeline, at, RequestOp.Start);
    public void Stop(int entity, ushort timeline) => RequestQueue.Add(_pending, entity, -1, timeline, 0, RequestOp.Stop);
    public void StopChannel(int entity, byte channel) => RequestQueue.Add(_pending, entity, -1, channel, 0, RequestOp.StopChannel);
    public void StopAll(int entity) => RequestQueue.Add(_pending, entity, -1, 0, 0, RequestOp.StopAll);
    public void Pause(int entity, ushort timeline) => RequestQueue.Add(_pending, entity, -1, timeline, 0, RequestOp.Pause);
    public void Resume(int entity, ushort timeline) => RequestQueue.Add(_pending, entity, -1, timeline, 0, RequestOp.Resume);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _kinds?.Dispose();
        NativeMemory.AlignedFree(_playing);
        RequestQueue.Free(_pending);
        RequestQueue.Free(_consumer);
        ClosingList.Free(_closing);
        NativeMemory.AlignedFree(_slots);
        NativeMemory.AlignedFree(_counts);
        GC.SuppressFinalize(this);
    }
}
