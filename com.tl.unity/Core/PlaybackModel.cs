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

public readonly struct EntityRef(int index, int version)
{
    public readonly int Index = index;
    public readonly int Version = version;
}

public readonly unsafe struct PlaybackView
{
    internal PlaybackView(Playback* slots, int* counts, ulong* spilled, Playback* blocks, int entities, int slotsPerEntity, ulong* dense = null, ushort* origins = null, ushort* blockOrigins = null, ushort clock = 0, ushort lag = 0, bool lagForward = true)
    {
        Slots = slots;
        Counts = counts;
        Spilled = spilled;
        Blocks = blocks;
        EntityCount = entities;
        SlotsPerEntity = slotsPerEntity;
        Dense = dense;
        Origins = origins;
        BlockOrigins = blockOrigins;
        Clock = clock;
        _lag = lag;
        _reverse = SlotWord.WorldReverse(lagForward);
    }

    public readonly Playback* Slots;
    public readonly int* Counts;
    public readonly ulong* Spilled;
    public readonly Playback* Blocks;
    public readonly int EntityCount;
    public readonly int SlotsPerEntity;
    public readonly ulong* Dense;
    public readonly ushort* Origins;
    public readonly ushort* BlockOrigins;
    public readonly ushort Clock;
    readonly int _lag;
    readonly ulong _reverse;

    public int CountOf(int entity) => Counts[entity];

    [MethodImpl(KindStore.Inline)]
    public bool IsDense(int entity) => Dense != null && (Dense[entity >> 6] >> (entity & 63) & 1) != 0;

    [MethodImpl(KindStore.Inline)]
    public Playback* RunOf(int entity)
        => (Spilled[entity >> 6] & (1ul << (entity & 63))) == 0
            ? Slots + (nuint)entity * (nuint)SlotsPerEntity
            : Blocks + *(int*)(Slots + (nuint)entity * (nuint)SlotsPerEntity);

    [MethodImpl(KindStore.Inline)]
    public Playback* SlotOf(int entity, int slot) => RunOf(entity) + slot;

    [MethodImpl(KindStore.Inline)]
    public ushort TickOf(Playback* slot)
    {
        var word = *(ulong*)slot;
        return (ushort)(slot->Tick + ((uint)(SlotWord.MovesForward(word, _reverse) ? _lag : -_lag) & (uint)SlotWord.PlainMask(word)));
    }
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
    internal readonly ushort* Next;
    readonly ushort* _due;
    ushort* _blockDue;
    int _limit;
    TimelineWorld? _owner;
    internal ushort Clock;
    internal bool Forward = true, Frozen;
    ulong* _dense;
    ushort* _origins;
    ushort* _blockOrigins;
    internal ushort DenseClock;
    internal bool Settled;
    int _wait = -1, _back = 32, _seen, _total;
    internal int Landed;
    internal bool Resting => _wait >= 0;

    public PlaybackTable(int entityCapacity, int slotsPerEntity = 4) : this(entityCapacity, slotsPerEntity, 0)
    {
    }

    public PlaybackTable(int entityCapacity, int slotsPerEntity, int horizonLimit)
    {
        if (entityCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        if (slotsPerEntity <= 0 || slotsPerEntity > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slotsPerEntity));
        if ((uint)horizonLimit > Horizon.Max) throw new ArgumentOutOfRangeException(nameof(horizonLimit));
        if (horizonLimit != 0 && (!BitConverter.IsLittleEndian || sizeof(Playback) != 8)) throw new PlatformNotSupportedException("Glide requires little-endian 8-byte playback storage.");
        _entities = entityCapacity;
        _slotsPerEntity = slotsPerEntity;
        _slots = (Playback*)KindStore.Zeroed((nuint)entityCapacity * (nuint)slotsPerEntity * 8u);
        _counts = (int*)KindStore.Zeroed((nuint)entityCapacity * 4u);
        _spilledWords = (entityCapacity + 63) >> 6;
        _spilled = (ulong*)KindStore.Zeroed((nuint)_spilledWords * 8u);
        _freeHeads = new int[9];
        for (var i = 0; i < _freeHeads.Length; i++) _freeHeads[i] = -1;
        _playingWords = (entityCapacity + 63) >> 6;
        _playing = (ulong*)KindStore.Zeroed((nuint)_playingWords * 8u);
        _consumer = RequestQueue.Create();
        _pending = RequestQueue.Create();
        _closing = ClosingList.Create();
        if (horizonLimit != 0)
        {
            try
            {
                Next = (ushort*)KindStore.Zeroed((nuint)entityCapacity * (nuint)(slotsPerEntity + 1) * 2u);
                _due = Next + entityCapacity;
                _limit = horizonLimit;
            }
            catch { Dispose(); throw; }
        }
    }

    public const int MaxSlots = 256;

    public int EntityCapacity => _entities;
    public KindStore? Kinds => _kinds;
    public long DroppedStarts { get; private set; }
    public ulong* Playing { get { Escape(); return _playing; } }
    internal ulong* PlayingBits => _playing;
    public int PlayingWords => _playingWords;

    internal void SetPlaying(int entity, bool value)
    {
        if (value) _playing[entity >> 6] |= 1ul << (entity & 63);
        else _playing[entity >> 6] &= ~(1ul << (entity & 63));
    }

    internal void AttachKinds(KindStore? kinds)
    {
        if (kinds != null && _kinds != null) throw new InvalidOperationException("A KindStore is already attached to this PlaybackTable.");
        Thaw();
        _kinds = kinds;
    }

    public int SlotsPerEntity => _slotsPerEntity;
    public PlaybackView View { get { Escape(); SettleAll(); return RawView; } }
    public PlaybackView SystemView => RawView;
    public ulong* SystemPlaying { get { SettleAll(); return _playing; } }
    internal PlaybackView RawView => new(_slots, _counts, _spilled, _blocks, _entities, _slotsPerEntity, _dense, _origins, _blockOrigins, DenseClock, Frozen ? Clock : (ushort)0, Forward);
    internal ulong* DenseBits => _dense;

    internal bool IsDense(int entity) => _dense != null && (_dense[entity >> 6] >> (entity & 63) & 1) != 0;

    internal void SetDense(int entity, bool value)
    {
        if (value) _dense[entity >> 6] |= 1ul << (entity & 63);
        else _dense[entity >> 6] &= ~(1ul << (entity & 63));
    }

    internal ushort* OriginsOf(int entity) => IsSpilled(entity) ? _blockOrigins + *(int*)InlineRun(entity) : _origins + (nuint)entity * (nuint)_slotsPerEntity;

    internal ushort* InlineDues => _origins + (nuint)_entities * (nuint)_slotsPerEntity;

    internal ushort* DuesOf(int entity) => IsSpilled(entity) ? _blockOrigins + (nuint)_blocksCapacity + (nuint)(*(int*)InlineRun(entity)) : _origins + (nuint)_entities * (nuint)_slotsPerEntity + (nuint)entity * (nuint)_slotsPerEntity;

    internal void EnsureDense()
    {
        if (_dense != null) return;
        _dense = (ulong*)KindStore.Zeroed((nuint)_playingWords * 8u);
        _origins = (ushort*)KindStore.Zeroed((nuint)_entities * (nuint)_slotsPerEntity * 4u);
        if (_blocksCapacity != 0) _blockOrigins = (ushort*)KindStore.Zeroed((nuint)_blocksCapacity * 4u);
    }

    public void Settle(int entity)
    {
        if (IsDense(entity)) _kinds!.Settle(entity);
    }

    void SettleAll()
    {
        if (_dense == null || Settled || _kinds == null) return;
        for (var w = 0; w < _playingWords; w++)
            for (var bits = _dense[w]; bits != 0; bits &= bits - 1) _kinds.Settle((w << 6) + KindStore.Tz(bits));
        Settled = true;
    }
    public int HorizonLimit => _limit;
    ushort* InlineDue(int entity) => _due + (nuint)entity * (nuint)_slotsPerEntity;
    internal ushort* DueOf(int entity) => IsSpilled(entity) ? _blockDue + *(int*)InlineRun(entity) : InlineDue(entity);

    void Escape() { Thaw(); _limit = 0; }

    internal void Edit(int entity)
    {
        if (IsDense(entity)) _kinds!.Undense(entity);
        if (Frozen) LazyTicks.Shift(SlotsOf(entity), _counts[entity], Clock, Forward);
    }

    internal void Seal(int entity)
    {
        if (!Frozen) return;
        var view = _owner!._view;
        var slots = SlotsOf(entity);
        var due = DueOf(entity);
        for (var i = 0; i < _counts[entity]; i++) due[i] = LazyTicks.Settle(slots + i, view.Timelines, view.TimelineCount, Clock, Forward);
        Next[entity] = (ushort)(Clock + 1);
    }

    internal void Thaw(int entity = -1, int slot = 0)
    {
        if (!Frozen) return;
        Frozen = false;
        for (var e = 0; e < _entities; e++)
        {
            var slots = SlotsOf(e);
            var count = _counts[e];
            if (e != entity) LazyTicks.Shift(slots, count, e < entity ? Clock + 1 : Clock, Forward);
            else
            {
                LazyTicks.Shift(slots, slot, Clock + 1, Forward);
                LazyTicks.Shift(slots + slot + 1, count - slot - 1, Clock, Forward);
            }
        }
    }

    internal bool Lazy(TimelineWorld? world, bool forward)
    {
        if (_limit == 0 || world == null || _wait >= 0 && _wait-- > 0) { Thaw(); return false; }
        if (_owner != world || Forward != forward) Thaw();
        _owner = world;
        Forward = forward;
        if (Frozen) return true;
        Frozen = true;
        Landed = _total = 0;
        _seen = -8;
        for (var e = 0; e < _entities; e++) { Seal(e); _total += _counts[e]; }
        return true;
    }

    internal void Judge()
    {
        if (++_seen <= 0) Landed = 0;
        if (_seen < 16) return;
        if (Landed > _total && _total >= 64) { Thaw(); _wait = _back; _back = Math.Min(_back * 2, 4096); }
        else _back = 32;
        _seen = Landed = 0;
    }

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

    internal long NativeBytes =>
        (long)_entities * _slotsPerEntity * 8
        + (long)_entities * 4
        + (long)_spilledWords * 8
        + (long)_blocksCapacity * 8
        + (Next == null ? 0 : ((long)_entities * (_slotsPerEntity + 1) + _blocksCapacity) * 2);
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
        for (; size > 1; size >>= 1) index++;
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
        Reserve(_blocksUsed + size);
        var offset = _blocksUsed;
        _blocksUsed += size;
        NativeMemory.Clear(_blocks + (nuint)offset, (nuint)size * 8u);
        return offset;
    }

    void Reserve(int needed)
    {
        if (needed <= _blocksCapacity) return;
        var capacity = Math.Max(_blocksCapacity == 0 ? 256 : _blocksCapacity * 2, needed);
        var blocks = (Playback*)NativeMemory.AlignedAlloc((nuint)capacity * 8u, 64u);
        if (_blocks != null)
        {
            new ReadOnlySpan<Playback>(_blocks, _blocksUsed).CopyTo(new Span<Playback>(blocks, _blocksUsed));
            NativeMemory.AlignedFree(_blocks);
        }
        if (_origins != null)
        {
            var origins = (ushort*)KindStore.Zeroed((nuint)capacity * 4u);
            if (_blockOrigins != null)
            {
                new ReadOnlySpan<ushort>(_blockOrigins, _blocksUsed).CopyTo(new Span<ushort>(origins, _blocksUsed));
                new ReadOnlySpan<ushort>(_blockOrigins + _blocksCapacity, _blocksUsed).CopyTo(new Span<ushort>(origins + capacity, _blocksUsed));
                NativeMemory.AlignedFree(_blockOrigins);
            }
            _blockOrigins = origins;
        }
        _blocks = blocks;
        _blocksCapacity = capacity;
        if (Next != null)
        {
            var due = (ushort*)NativeMemory.AlignedAlloc((nuint)capacity * 2u, 64u);
            new ReadOnlySpan<ushort>(_blockDue, _blocksUsed).CopyTo(new Span<ushort>(due, _blocksUsed));
            NativeMemory.AlignedFree(_blockDue);
            _blockDue = due;
        }
    }

    void FreeBlock(int offset, int capacity)
    {
        var index = SizeIndex(capacity);
        *(int*)(_blocks + (nuint)offset) = _freeHeads[index];
        _freeHeads[index] = offset;
    }

    void Unspill(int entity, Playback* inline, int count = -1)
    {
        var offset = *(int*)inline;
        var capacity = *(int*)((byte*)inline + 4);
        if (count < 0) count = _counts[entity];
        new ReadOnlySpan<Playback>(_blocks + (nuint)offset, count).CopyTo(new Span<Playback>(inline, count));
        if (_blockDue != null) new ReadOnlySpan<ushort>(_blockDue + (nuint)offset, count).CopyTo(new Span<ushort>(InlineDue(entity), count));
        _spilled[entity >> 6] &= ~(1ul << (entity & 63));
        FreeBlock(offset, capacity);
    }

    internal Playback* Reserve(int entity, int length, int used = 0)
    {
        EnterGate();
        try
        {
            var inline = InlineRun(entity);
            var count = Math.Max(_counts[entity], used);
            var needed = Math.Max(length, count);
            if (needed <= _slotsPerEntity)
            {
                if (IsSpilled(entity)) Unspill(entity, inline, count);
                return inline;
            }
            var size = NextPow2(Math.Min(needed, MaxSlots));
            int oldOff = -1, oldCap = 0;
            if (IsSpilled(entity))
            {
                oldOff = *(int*)inline;
                oldCap = *(int*)((byte*)inline + 4);
                if (oldCap >= Math.Min(needed, MaxSlots)) return _blocks + (nuint)oldOff;
            }
            var newOffset = AllocBlock(size);
            var src = oldOff >= 0 ? _blocks + (nuint)oldOff : inline;
            new ReadOnlySpan<Playback>(src, count).CopyTo(new Span<Playback>(_blocks + (nuint)newOffset, count));
            if (oldOff >= 0) FreeBlock(oldOff, oldCap);
            else _spilled[entity >> 6] |= 1ul << (entity & 63);
            *(int*)inline = newOffset;
            *(int*)((byte*)inline + 4) = size;
            return _blocks + (nuint)newOffset;
        }
        finally { ExitGate(); }
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
        Settle(entity);
        var slots = new Slots(this, entity);
        var found = PlaybackRules.Find(ref slots, channel, out playback);
        if (Frozen) fixed (Playback* at = &playback) LazyTicks.Shift(at, 1, Clock, Forward);
        return found;
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
            [MethodImpl(KindStore.Inline)]
            get => _table._counts[_entity];
        }

        public ref Playback this[int index]
        {
            [MethodImpl(KindStore.Inline)]
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
#if TL_CHECKED
            _table._kinds?.ValidateStart(_entity, timeline);
#else
            if (_table._kinds is { } kinds && !kinds.Reaches(_entity, timeline)) { _table.DroppedStarts++; return false; }
#endif
            return true;
        }

        public void Close(int index)
        {
            _table.CloseSlot(_entity, index);
            _items = _table.SlotsOf(_entity);
        }

        public void Purge(int index) => _table._kinds?.Purge(_entity, index, in _items[index]);
    }

    void Add(int entity, ushort timeline, ushort at, RequestOp op) => RequestQueue.Add(_pending, entity, -1, timeline, at, op);
    public void Start(int entity, ushort timeline, ushort at = 0) => Add(entity, timeline, at, RequestOp.Start);
    public void Stop(int entity, ushort timeline) => Add(entity, timeline, 0, RequestOp.Stop);
    public void StopChannel(int entity, byte channel) => Add(entity, channel, 0, RequestOp.StopChannel);
    public void StopAll(int entity) => Add(entity, 0, 0, RequestOp.StopAll);
    public void Pause(int entity, ushort timeline) => Add(entity, timeline, 0, RequestOp.Pause);
    public void Resume(int entity, ushort timeline) => Add(entity, timeline, 0, RequestOp.Resume);
    public void Seek(int entity, ushort timeline, ushort tick) => Add(entity, timeline, tick, RequestOp.Seek);
    public void Reverse(int entity, ushort timeline, bool reverse) => Add(entity, timeline, reverse ? (ushort)1 : (ushort)0, RequestOp.Reverse);
    public void PauseChannel(int entity, byte channel) => Add(entity, channel, 0, RequestOp.PauseChannel);
    public void ResumeChannel(int entity, byte channel) => Add(entity, channel, 0, RequestOp.ResumeChannel);
    public void SeekChannel(int entity, byte channel, ushort tick) => Add(entity, channel, tick, RequestOp.SeekChannel);
    public void ReverseChannel(int entity, byte channel, bool reverse) => Add(entity, channel, reverse ? (ushort)1 : (ushort)0, RequestOp.ReverseChannel);
    public void SetSpeed(int entity, ushort timeline, ushort sixteenths) => Add(entity, timeline, sixteenths, RequestOp.Speed);
    public void SetChannelSpeed(int entity, byte channel, ushort sixteenths) => Add(entity, channel, sixteenths, RequestOp.SpeedChannel);
    public void Reset(int entity) => Add(entity, 0, 0, RequestOp.Reset);

    public void Save(PlaybackImage image) => Image(image, true);

    public void Restore(PlaybackImage image) => Image(image, false);

    void Image(PlaybackImage m, bool saving)
    {
        if (m is null) throw new ArgumentNullException(nameof(m));
        m.Begin(saving);
        Thaw();
        _kinds?.Sync();
        _kinds?.UndenseAll();
        m.Same(_entities);
        m.Same(_slotsPerEntity);
        m.Same((long)LibraryGeneration);
        m.Same(_kinds?.Shape ?? -1);
        m.Io(_slots, (long)_entities * _slotsPerEntity * 8);
        m.Io(_counts, _entities * 4L);
        m.Io(_spilled, _spilledWords * 8L);
        m.Io(_playing, _playingWords * 8L);
        var used = m.Of(_blocksUsed);
        Reserve(used);
        m.Io(_blocks, used * 8L);
        _blocksUsed = used;
        fixed (int* heads = _freeHeads) m.Io(heads, _freeHeads.Length * 4L);
        Clock = m.Of(Clock);
        DenseClock = m.Of(DenseClock);
        DroppedStarts = m.Of(DroppedStarts);
        _wait = m.Of(_wait);
        _back = m.Of(_back);
        RequestQueue.Image(_consumer, m);
        RequestQueue.Image(_pending, m);
        ClosingList.Image(_closing, m);
        _kinds?.Image(m);
        m.End();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _kinds?.Dispose();
        RequestQueue.Free(_pending);
        RequestQueue.Free(_consumer);
        ClosingList.Free(_closing);
        KindStore.Free4(_playing, _slots, _counts, _spilled);
        KindStore.Free4(Next, _blockDue, _dense, _origins);
        KindStore.Free4(_blockOrigins, _blocks, null, null);
        GC.SuppressFinalize(this);
    }
}
