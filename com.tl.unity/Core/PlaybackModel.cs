using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

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
    ReenterPending = 1 << 7,
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct Playback
{
    public ushort Timeline;
    public ushort Tick;
    public byte Channel;
    public PlaybackFlags Flags;
    public byte Rate;
    public byte Phase;
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
    internal PlaybackView(Playback* slots, int* counts, ulong* spilled, Playback* blocks, int entities, int slotsPerEntity)
    {
        Slots = slots;
        Counts = counts;
        Spilled = spilled;
        Blocks = blocks;
        EntityCount = entities;
        SlotsPerEntity = slotsPerEntity;
    }

    public readonly Playback* Slots;
    public readonly int* Counts;
    public readonly ulong* Spilled;
    public readonly Playback* Blocks;
    public readonly int EntityCount;
    public readonly int SlotsPerEntity;

    public int CountOf(int entity) => Counts[entity];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Playback* RunOf(int entity)
        => (Spilled[entity >> 6] & (1ul << (entity & 63))) == 0
            ? Slots + (nuint)entity * (nuint)SlotsPerEntity
            : Blocks + *(int*)(Slots + (nuint)entity * (nuint)SlotsPerEntity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Playback* SlotOf(int entity, int slot) => RunOf(entity) + slot;
}

public sealed unsafe class PlaybackTable : IDisposable
{
    Playback* _slots;
    readonly int* _counts;
    readonly int _entities;
    readonly int _slotsPerEntity;
    readonly ulong* _spilled;
    readonly int _spilledWords;
    Playback* _blocks;
    int _blocksCapacity;
    int _blocksUsed;
    readonly int[] _freeHeads;
    readonly ulong* _playing;
    readonly int _playingWords;
    KindStore? _kinds;
    readonly RequestQueue* _consumer;
    readonly RequestQueue* _pending;
    readonly ClosingList* _closing;
    int _growGate;
    bool _disposed;
    internal ulong LibraryGeneration;

    public PlaybackTable(int entityCapacity, int slotsPerEntity = 4)
    {
        if (entityCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        if (slotsPerEntity <= 0 || slotsPerEntity > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slotsPerEntity));
        _entities = entityCapacity;
        _slotsPerEntity = slotsPerEntity;
        var slots = NativeMemory.AlignedAlloc((nuint)entityCapacity * (nuint)slotsPerEntity * 8u, 64u);
        var counts = NativeMemory.AlignedAlloc((nuint)entityCapacity * 4u, 8u);
        NativeMemory.Clear(slots, (nuint)entityCapacity * (nuint)slotsPerEntity * 8u);
        NativeMemory.Clear(counts, (nuint)entityCapacity * 4u);
        _slots = (Playback*)slots;
        _counts = (int*)counts;
        _spilledWords = (entityCapacity + 63) >> 6;
        _spilled = (ulong*)NativeMemory.AlignedAlloc((nuint)_spilledWords * 8u, 8u);
        NativeMemory.Clear(_spilled, (nuint)_spilledWords * 8u);
        _freeHeads = new int[9];
        for (var i = 0; i < _freeHeads.Length; i++) _freeHeads[i] = -1;
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
    public ulong* Playing => _playing;
    public int PlayingWords => _playingWords;

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
    public PlaybackView View => new(_slots, _counts, _spilled, _blocks, _entities, _slotsPerEntity);
    public RequestQueue* Pending => _pending;
    public RequestQueue* Consumer => _consumer;
    public ClosingList* Closing => _closing;

    public int CountOf(int entity) => _counts[entity];

    internal bool IsSpilled(int entity) => (_spilled[entity >> 6] & (1ul << (entity & 63))) != 0;

    internal int RunCapacity(int entity)
    {
        var inline = InlineRun(entity);
        return IsSpilled(entity) ? *(int*)((byte*)inline + 4) : _slotsPerEntity;
    }

    // Playback storage only: inline slots, counts, the Spilled bitset and
    // the Blocks arena capacity. Excludes the Playing bitset, the request
    // queues and the closing list.
    internal long NativeBytes =>
        (long)_entities * _slotsPerEntity * 8
        + (long)_entities * 4
        + (long)_spilledWords * 8
        + (long)_blocksCapacity * 8;
    public bool IsPlaying(int entity, ushort timeline)
    {
        var slots = new Slots(this, entity);
        return PlaybackRules.IsPlaying(ref slots, timeline);
    }

    void EnterGate() { var spin = new SpinWait(); while (Interlocked.CompareExchange(ref _growGate, 1, 0) != 0) spin.SpinOnce(); }
    void ExitGate() => Volatile.Write(ref _growGate, 0);

    Playback* InlineRun(int entity) => _slots + (nuint)entity * (nuint)_slotsPerEntity;

    static int NextPow2(int needed)
    {
        var size = 2;
        while (size < needed) size <<= 1;
        return size;
    }

    static int SizeIndex(int size)
    {
        var index = 0;
        while (size > 1)
        {
            size >>= 1;
            index++;
        }
        return index;
    }

    int AllocBlock(int size)
    {
        var index = SizeIndex(size);
        var head = _freeHeads[index];
        if (head >= 0)
        {
            _freeHeads[index] = *(int*)(_blocks + (nuint)head);
            NativeMemory.Clear(_blocks + (nuint)head, (nuint)size * 8u);
            return head;
        }
        if (_blocksUsed + size > _blocksCapacity)
        {
            var needed = _blocksUsed + size;
            var capacity = _blocksCapacity == 0 ? 256 : Math.Max(_blocksCapacity * 2, needed);
            var blocks = (Playback*)NativeMemory.AlignedAlloc((nuint)capacity * 8u, 64u);
            if (_blocks != null)
            {
                new ReadOnlySpan<Playback>(_blocks, _blocksUsed).CopyTo(new Span<Playback>(blocks, _blocksUsed));
                NativeMemory.AlignedFree(_blocks);
            }
            _blocks = blocks;
            _blocksCapacity = capacity;
        }
        var offset = _blocksUsed;
        _blocksUsed += size;
        NativeMemory.Clear(_blocks + (nuint)offset, (nuint)size * 8u);
        return offset;
    }

    void FreeBlock(int offset, int capacity)
    {
        var index = SizeIndex(capacity);
        *(int*)(_blocks + (nuint)offset) = _freeHeads[index];
        _freeHeads[index] = offset;
    }

    void Unspill(int entity, Playback* inline)
    {
        var offset = *(int*)inline;
        var capacity = *(int*)((byte*)inline + 4);
        var count = _counts[entity];
        new ReadOnlySpan<Playback>(_blocks + (nuint)offset, count).CopyTo(new Span<Playback>(inline, count));
        _spilled[entity >> 6] &= ~(1ul << (entity & 63));
        FreeBlock(offset, capacity);
    }

    // Ensures one entity's run holds at least length playbacks and returns
    // the run pointer. Other entities' runs are untouched.
    internal Playback* Reserve(int entity, int length)
    {
        EnterGate();
        try
        {
            var inline = InlineRun(entity);
            var count = _counts[entity];
            var needed = Math.Max(length, count);
            if (needed <= _slotsPerEntity)
            {
                if (IsSpilled(entity)) Unspill(entity, inline);
                return inline;
            }
            var size = NextPow2(Math.Min(needed, MaxSlots));
            if (IsSpilled(entity))
            {
                var offset = *(int*)inline;
                var capacity = *(int*)((byte*)inline + 4);
                if (capacity >= Math.Min(needed, MaxSlots)) return _blocks + (nuint)offset;
                var grown = size;
                var newOffset = AllocBlock(grown);
                new ReadOnlySpan<Playback>(_blocks + (nuint)offset, count).CopyTo(new Span<Playback>(_blocks + (nuint)newOffset, count));
                FreeBlock(offset, capacity);
                *(int*)inline = newOffset;
                *(int*)((byte*)inline + 4) = grown;
                return _blocks + (nuint)newOffset;
            }
            var block = size;
            var blockOffset = AllocBlock(block);
            new ReadOnlySpan<Playback>(inline, count).CopyTo(new Span<Playback>(_blocks + (nuint)blockOffset, count));
            *(int*)inline = blockOffset;
            *(int*)((byte*)inline + 4) = block;
            _spilled[entity >> 6] |= 1ul << (entity & 63);
            return _blocks + (nuint)blockOffset;
        }
        finally
        {
            ExitGate();
        }
    }

    internal void CloseSlot(int entity, int slot)
    {
        var slots = SlotsOf(entity);
        ClosingList.Add(_closing, entity, slot, in slots[slot]);
        if (_kinds == null) RemoveSlot(entity, slot);
        else _kinds.Close(entity, slot, ref slots[slot]);
    }

    internal void ResetEntity(int entity)
    {
        _kinds?.ResetEntity(entity);
        var slots = SlotsOf(entity);
        new Span<Playback>(slots, _counts[entity]).Clear();
        _counts[entity] = 0;
        if (IsSpilled(entity)) Unspill(entity, InlineRun(entity));
        new Span<Playback>(InlineRun(entity), _slotsPerEntity).Clear();
        SetPlaying(entity, false);
    }

    internal void RemoveSlot(int entity, int slot)
    {
        var slots = SlotsOf(entity);
        slots[slot] = default;
        var count = _counts[entity];
        while (count > 0 && slots[count - 1].Timeline == 0) count--;
        _counts[entity] = count;
        if (count <= _slotsPerEntity && IsSpilled(entity)) Unspill(entity, InlineRun(entity));
    }

    internal Playback* SlotsOf(int entity)
    {
        var inline = InlineRun(entity);
        return IsSpilled(entity) ? _blocks + *(int*)inline : inline;
    }

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
            if (count == MaxSlots) return false;
            if (count >= _table.RunCapacity(_entity) || (_table.IsSpilled(_entity) && count <= _table._slotsPerEntity))
                _items = _table.Reserve(_entity, count + 1);
            _items[count] = playback;
            _table._counts[_entity] = count + 1;
            return true;
        }

        public bool Admit(ushort timeline)
        {
            _table._kinds?.ValidateStart(_entity, timeline);
            return true;
        }

        public void Close(int index)
        {
            _table.CloseSlot(_entity, index);
            _items = _table.SlotsOf(_entity);
        }

        public void Purge(int index) => _table._kinds?.Purge(_entity, index, in _items[index]);
    }

    public void Start(int entity, ushort timeline, ushort at = 0) => RequestQueue.Add(_pending, entity, -1, timeline, at, RequestOp.Start);
    public void Stop(int entity, ushort timeline) => RequestQueue.Add(_pending, entity, -1, timeline, 0, RequestOp.Stop);
    public void StopChannel(int entity, byte channel) => RequestQueue.Add(_pending, entity, -1, channel, 0, RequestOp.StopChannel);
    public void StopAll(int entity) => RequestQueue.Add(_pending, entity, -1, 0, 0, RequestOp.StopAll);
    public void Pause(int entity, ushort timeline) => RequestQueue.Add(_pending, entity, -1, timeline, 0, RequestOp.Pause);
    public void Resume(int entity, ushort timeline) => RequestQueue.Add(_pending, entity, -1, timeline, 0, RequestOp.Resume);
    public void Seek(int entity, ushort timeline, ushort tick) => RequestQueue.Add(_pending, entity, -1, timeline, tick, RequestOp.Seek);
    public void Reverse(int entity, ushort timeline, bool reverse) => RequestQueue.Add(_pending, entity, -1, timeline, reverse ? (ushort)1 : (ushort)0, RequestOp.Reverse);
    public void PauseChannel(int entity, byte channel) => RequestQueue.Add(_pending, entity, -1, channel, 0, RequestOp.PauseChannel);
    public void ResumeChannel(int entity, byte channel) => RequestQueue.Add(_pending, entity, -1, channel, 0, RequestOp.ResumeChannel);
    public void SeekChannel(int entity, byte channel, ushort tick) => RequestQueue.Add(_pending, entity, -1, channel, tick, RequestOp.SeekChannel);
    public void ReverseChannel(int entity, byte channel, bool reverse) => RequestQueue.Add(_pending, entity, -1, channel, reverse ? (ushort)1 : (ushort)0, RequestOp.ReverseChannel);
    public void SetSpeed(int entity, ushort timeline, ushort sixteenths) => RequestQueue.Add(_pending, entity, -1, timeline, sixteenths, RequestOp.Speed);
    public void SetChannelSpeed(int entity, byte channel, ushort sixteenths) => RequestQueue.Add(_pending, entity, -1, channel, sixteenths, RequestOp.SpeedChannel);
    public void Reset(int entity) => RequestQueue.Add(_pending, entity, -1, 0, 0, RequestOp.Reset);

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
        NativeMemory.AlignedFree(_spilled);
        if (_blocks != null) NativeMemory.AlignedFree(_blocks);
        GC.SuppressFinalize(this);
    }
}
