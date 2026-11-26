namespace Tl;

public readonly struct KindStats(int rows, int maxLength, int overflows, long bytesPerRow)
{
    public readonly int Rows = rows;
    public readonly int MaxLength = maxLength;
    public readonly int Overflows = overflows;
    public readonly long BytesPerRow = bytesPerRow;
}

public readonly unsafe struct KindPulses<T> where T : unmanaged
{
    readonly KindStore.PulseHandle _handle;

    internal KindPulses(KindStore.PulseHandle handle) { _handle = handle; }

    public void To(int entity, T value) => _handle.Send(entity, &value);
}

public readonly unsafe struct KindPulses<T, TFold> where T : unmanaged where TFold : struct, IPulseFold<T>
{
    readonly KindStore.PulseHandle _handle;

    internal KindPulses(KindStore.PulseHandle handle) { _handle = handle; }

    public void To(int entity, T value) => _handle.Send<T, TFold>(entity, value);
}

internal unsafe struct PulseQueue
{
    public byte* Items;
    public int Count;
    public int Capacity;
    public int Stride;
}

public sealed unsafe partial class KindStore : IDisposable
{
    readonly TimelineWorld _world;
    readonly PlaybackTable _players;
    readonly string[]? _names;
    readonly ulong[]? _reach;
    ulong _generation;
    KindSectionView _sec;
    LibraryTimeline* _tls;
    int _timelineCount;
    int _kindCount;
    KindTable** _tables;
    PulseQueue* _queues;
    ulong* _mask;
    int* _offset;
    int* _rows;
    ulong* _closure;
    uint* _pairs;
    int* _pairStart;
    int* _windowBase;
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal readonly unsafe struct WindowSteps(LibraryStep** steps, int firstStage, int stageCount)
    {
        public readonly LibraryStep** Steps = steps;
        public readonly int FirstStage = firstStage;
        public readonly int StageCount = stageCount;

    }

    WindowSteps* _windows;
    int _windowTotal;
    KindTables _t;
    Rows _kindRows;
    KindScore _score;
    byte* _samples;
    ulong** _listed;
    KindValues* _values;
    uint* _spanMeta;
    int* _pulseBase;
    byte* _pulseBits;
    ulong* _pulseMasks;
    KindPulseBuffer* _pulseHits;
    KindPulseValues* _pulseValues;
    ulong* _closedEntity;
    ulong _tablePulses;
    ulong _tickTablePulses;
    ulong _slowPulseMask;
    bool _hasClosed;
    readonly Dictionary<(string, int), (nint Routes, int Part)> _joined = new();
    readonly HashSet<string> _feeding = new(StringComparer.Ordinal);
    readonly object _joinGate = new();
    ushort*[] _routes = new ushort*[0];
    List<int>?[] _parts = Array.Empty<List<int>?>();
    bool _disposed;
    string[] _kindNames = Array.Empty<string>();
    Dictionary<string, int> _kindIndex = new(StringComparer.Ordinal);
    int[] _valueBytes = Array.Empty<int>();
    byte[] _accepts = Array.Empty<byte>();
    bool[] _folds = Array.Empty<bool>();
    int[] _owner = Array.Empty<int>();
    int _epoch;

    internal sealed class PulseHandle
    {
        readonly KindStore _store;
        readonly string _name;
        int _kind;
        int _epoch;
        bool _folds;

        internal PulseHandle(KindStore store, string name, int kind)
        {
            _store = store;
            _name = name;
            _kind = kind;
            _epoch = store._epoch;
            _folds = store._folds[kind];
        }

        internal void Send(int entity, void* value)
        {
            Refresh();
            if (_kind < 0) _store.DroppedPulses++;
            else _store.Send(_kind, entity, value);
        }

        internal void Send<T, TFold>(int entity, T value) where T : unmanaged where TFold : struct, IPulseFold<T>
        {
            Refresh();
            if (_kind < 0) _store.DroppedPulses++;
            else if (_folds) _store.SendFolded<T, TFold>(_kind, entity, value);
            else _store.Send(_kind, entity, &value);
        }

        void Refresh()
        {
            if (_epoch == _store._epoch) return;
            _kind = _store.IndexOf(_name);
            _folds = _kind >= 0 && _store._folds[_kind];
            _epoch = _store._epoch;
        }
    }

    public KindStore(TimelineWorld world, PlaybackTable players, string[]? kindNames = null, ulong[]? reach = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        if (kindNames != null && (kindNames.Length > 64 || reach == null || reach.Length != players.EntityCapacity))
            throw new ArgumentException("kindNames holds at most 64 names and reach needs one mask per entity.");
        _names = kindNames;
        _reach = reach;
        Build();
        players.AttachKinds(this);
    }

    public int KindCount => _kindCount;

    public long DroppedPulses { get; private set; }

    public string NameOf(int kind) => _kindNames[kind];

    public int IndexOf(string name) => _kindIndex.TryGetValue(name, out var kind) ? kind : -1;

    public KindStats GetStats(int kind)
    {
        var t = _tables[kind];
        return t == null ? default : new KindStats(t->Rows, t->ColdMaxLength(), t->ColdOverflows(), t->StorageBytesPerRow);
    }

    public KindPulses<T> Pulses<T>(string kind) where T : unmanaged
    {
        var k = IndexOf(kind);
        if (k < 0) throw new ArgumentException($"The loaded library has no kind '{kind}'.");
        if (_accepts[k] == (byte)KindEntries.Spans) throw new ArgumentException($"Kind '{kind}' accepts spans only; game code cannot send it pulses.");
        if (_valueBytes[k] != sizeof(T)) throw new ArgumentException($"Kind '{kind}' takes {_valueBytes[k]}-byte values; {typeof(T).Name} is {sizeof(T)}.");
        return new KindPulses<T>(new PulseHandle(this, kind, k));
    }

    public KindPulses<T, TFold> Pulses<T, TFold>(string kind) where T : unmanaged where TFold : struct, IPulseFold<T>
    {
        _ = Pulses<T>(kind);
        var k = IndexOf(kind);
        if (!_folds[k]) throw new ArgumentException($"Kind '{kind}' does not set FoldsPulses; set it on the kind, or send with Pulses<T>.");
        return new KindPulses<T, TFold>(new PulseHandle(this, kind, k));
    }

    internal void SendFolded<T, TFold>(int kind, int entity, T value) where T : unmanaged where TFold : struct, IPulseFold<T>
    {
        var q = _queues + kind;
        if (q->Count > 0)
        {
            var last = q->Items + (nuint)(q->Count - 1) * (nuint)q->Stride;
            if (*(int*)last == entity)
            {
                var total = Unsafe.ReadUnaligned<T>(last + 4);
                default(TFold).Fold(ref total, in value);
                CopyValue(last + 4, (byte*)&total, sizeof(T));
                return;
            }
        }
        Send(kind, entity, &value);
    }

    internal void Send(int kind, int entity, void* value)
    {
        if ((uint)entity >= (uint)_players.EntityCapacity) throw new ArgumentOutOfRangeException(nameof(entity));
        var q = _queues + kind;
        if (q->Count == q->Capacity)
        {
            var capacity = q->Capacity == 0 ? 16 : q->Capacity * 2;
            var items = (byte*)NativeMemory.AlignedAlloc((nuint)capacity * (nuint)q->Stride, 8);
            if (q->Count > 0) new ReadOnlySpan<byte>(q->Items, q->Count * q->Stride).CopyTo(new Span<byte>(items, capacity * q->Stride));
            NativeMemory.AlignedFree(q->Items);
            q->Items = items;
            q->Capacity = capacity;
        }
        var at = q->Items + (nuint)q->Count * (nuint)q->Stride;
        *(int*)at = entity;
        CopyValue(at + 4, (byte*)value, _valueBytes[kind]);
        q->Count++;
    }

    public KindTable* Table(int kind) => (uint)kind < (uint)_kindCount ? _tables[kind] : null;

    public KindScore Score => _score;

    public KindValues* SpanValues<TFill>(int kind, int part, ref TFill fill) where TFill : struct, IKindFill
    {
        if ((uint)kind >= (uint)_kindCount) return null;
        var v = _values + kind;
        var ptr = Volatile.Read(ref *(nint*)&v->Data);
        if (ptr == default)
        {
            lock (_joinGate)
            {
                if (v->Data == null)
                {
                    var views = (KindValues*)Zeroed((nuint)64 * (nuint)sizeof(KindValues));
                    var row = (int*)Zeroed((nuint)Math.Max(1, _score.TimelineCount) * 4);
                    byte* data = null;
                    var committed = false;
                    try
                    {
                        var b = KindBuild.Of(in _score, _tls, views, row, false, ref fill, true);
                        if (b.Kinds != 0)
                        {
                            var valid = _kindCount == 64 ? ~0ul : (1ul << _kindCount) - 1;
                            if ((b.Kinds & ~valid) != 0) throw new InvalidOperationException("Tl: kind out of range.");
                            for (var k = 0; k < _kindCount; k++)
                                if ((b.Kinds >> k & 1) != 0 && _values[k].Data != null)
                                    throw new InvalidOperationException("Tl: overlapping span table.");
                            var half = (nuint)b.Rows * (nuint)b.Stride;
                            data = (byte*)Zeroed(half * (b.Split ? 2u : 1u));
                            b.Fill(data, ref fill);
                            for (var r = 0; b.Split && r < b.Rows; r++) *(uint*)(data + half + (nuint)r * (nuint)b.Stride) = *(uint*)(data + (nuint)r * (nuint)b.Stride);
                            for (var k = 0; k < _kindCount; k++)
                                if ((b.Kinds >> k & 1) != 0)
                                {
                                    views[k].Data = null;
                                    _values[k] = views[k];
                                    Volatile.Write(ref *(nint*)&_values[k].Data, (nint)data);
                                }
                            committed = true;
                            _denseEpoch++;
                        }
                    }
                    finally
                    {
                        if (!committed)
                        {
                            NativeMemory.AlignedFree(row);
                            NativeMemory.AlignedFree(data);
                        }
                        NativeMemory.AlignedFree(views);
                    }
                }
                ptr = (nint)v->Data;
            }
        }
        return ptr != default && v->Part == part ? v : null;
    }

    public long SpanValueBytes(int kind) => (uint)kind < (uint)_kindCount && _values[kind].Data != null ? _values[kind].Bytes : 0;

    public KindPulseValues* PulseValues<T, TEval>(int kind, int part, ref TEval eval)
        where T : unmanaged where TEval : struct, IKindSpanValues<T>
    {
        if ((uint)kind >= (uint)_kindCount) return null;
        var v = _pulseValues + kind;
        var ptr = Volatile.Read(ref *(nint*)&v->Data);
        if (ptr == default && Volatile.Read(ref v->Cells) >= 0)
        {
            lock (_joinGate)
            {
                if (v->Data == null && v->Cells >= 0)
                {
                    if (PulseHitsEnabled && _routes[kind] != null && RowsAreEntities(kind) && !_feeding.Contains(_kindNames[kind]))
                    {
                        var steps = new Steps { Store = this };
                        var built = KindPulseValues.Build<T, TEval, Steps>(in _t, kind, part, _routes[kind], ref eval, ref steps);
                        var data = built.Data;
                        built.Data = null;
                        *v = built;
                        Volatile.Write(ref *(nint*)&v->Data, (nint)data);
                        _tablePulses |= 1ul << kind;
                    }
                    else Volatile.Write(ref v->Cells, -1);
                }
                ptr = (nint)v->Data;
            }
        }
        return (_tickTablePulses >> kind & 1) != 0 && ptr != default && v->Part == part ? v : null;
    }

    public KindPulseBuffer* PulseHitsOf(int kind) => (uint)kind < (uint)_kindCount ? _pulseHits + kind : null;

    public void ClearPulseHits(int kind, int limit)
    {
        if ((uint)kind >= (uint)_kindCount) return;
        var buf = _pulseHits + kind;
        if (limit >= _players.EntityCapacity)
        {
            if (buf->Capacity > Math.Max(4096, buf->Count * 8)) buf->Free();
            buf->Count = 0;
            return;
        }
        if (buf->Count == 0) return;
        var lo = buf->LowerBound(limit);
        var rem = buf->Count - lo;
        if (rem > 0 && lo > 0) Buffer.MemoryCopy(buf->Items + lo, buf->Items, (long)rem * sizeof(KindPulseHit), (long)rem * sizeof(KindPulseHit));
        buf->Count = rem;
    }

    public long PulseValueBytes(int kind) => (uint)kind < (uint)_kindCount && (_tablePulses >> kind & 1) != 0 && _pulseValues[kind].Data != null ? _pulseValues[kind].Bytes : 0;

    public bool HasSamples => _samples[_players.EntityCapacity] != 0;

    public ulong* Playing => _players.PlayingBits;

    public int SamplesOf(int entity) => _samples[entity];

    public ulong* ListedOf(int kind) => _listed[kind];

    public bool RowsAreEntities(int kind) => _tables[kind] != null && _tables[kind]->Rows == _players.EntityCapacity;

    public static int TrailingZeros(ulong value) => Tz(value);
    [MethodImpl(Inline)]
    public static void Pick(ref nuint at, ref long best, uint head, long priority, nuint row)
    {
        var mask = 0ul - ((ulong)(head & 1u) & ((ulong)(priority - best) >> 63 ^ 1ul));
        at ^= (at ^ row) & (nuint)mask;
        best ^= (best ^ priority) & (long)mask;
    }

    struct Steps : IKindSteps
    {
        public KindStore Store;

        public LibraryStep* StepOf(ushort timeline, int window, ushort tick) => Store.StepOf(timeline, window, tick);
    }

    public LibraryStep* StepOf(ushort timeline, int window, ushort tick)
    {
        var t = timeline - 1;
        if ((uint)t >= (uint)_timelineCount) return null;
        var w = _windows[_windowBase[t] + window];
        var arr = w.Steps;
        if (arr == null) return null;
        if (w.StageCount == 1) return arr[0];
        var tl = _tls + t;
        var stage = LibraryViewReader.StageAt(tl, tick);
        if (stage == null) return null;
        var s = (int)(stage - tl->Stages) - w.FirstStage;
        return (uint)s < (uint)w.StageCount ? arr[s] : null;
    }

    public uint MarkerOf(ushort timeline, int window)
    {
        var tk = _sec.TimelineOf(timeline);
        return tk == null ? uint.MaxValue : _sec.Windows(tk)[window].Pair;
    }

    public ushort* Join(string kind, int valueBytes, string signature, int key, out int part)
    {
        lock (_joinGate)
        {
            part = 0;
            if (_joined.TryGetValue((kind, key), out var cached))
            {
                part = cached.Part;
                return (ushort*)cached.Routes;
            }
            var k = IndexOf(kind);
            if (k < 0) return null;
            if (_valueBytes[k] != valueBytes) throw new InvalidOperationException($"Kind '{kind}' is {valueBytes} bytes in code but {_valueBytes[k]} in the loaded library; rebake the library with tl.");
            var library = new List<string>();
            var ordinals = new List<int>();
            for (var f = 0; f < _sec.FeederCount; f++)
            {
                if (_sec.Feeders[f].Kind != k) continue;
                library.Add(_sec.Text(_sec.Feeders[f].ConsumerOffset, _sec.Feeders[f].ConsumerLength) + "." + _sec.Text(_sec.Feeders[f].MethodOffset, _sec.Feeders[f].MethodLength) + ":" + _sec.Feeders[f].Trigger);
                ordinals.Add(_sec.Feeders[f].Ordinal);
            }
            var group = _sec.Kinds[k].GroupNameLength != 0 ? "@" + _sec.GroupOf(k) : null;
            var lines = signature.Length == 0 ? new List<string>() : new List<string>(signature.Split('\n'));
            var routes = _routes[k];
            if (routes == null)
            {
                routes = (ushort*)Zeroed(256 * 2);
                for (var i = 0; i < 256; i++) routes[i] = 0xFFFF;
                _routes[k] = routes;
            }
            var parts = _parts[k] ??= new List<int>();
            var differs = group != null && (lines.Count == 0 || lines[0] != group);
            if (group != null && !differs) lines.RemoveAt(0);
            var at = new int[lines.Count];
            for (var i = 0; i < lines.Count && !differs; i++)
            {
                at[i] = library.IndexOf(lines[i]);
                differs = at[i] < 0 || routes[ordinals[at[i]]] != 0xFFFF;
            }
            if (differs || key == 0 && parts.Count == 0 && lines.Count != library.Count && !_feeding.Contains(kind))
            {
                var have = (group == null ? "" : group + " ") + string.Join(" ", library);
                throw new InvalidOperationException($"Kind '{kind}' feeders in the loaded library differ from the compiled game; rebake the library with tl.\nlibrary: {have}\ncode:    {signature.Replace('\n', ' ')}");
            }
            part = parts.Count;
            parts.Add(key);
            for (var i = 0; i < lines.Count; i++) routes[ordinals[at[i]]] = (ushort)((part << 8) | i);
            if (key != 0) { _feeding.Add(kind); _tablePulses &= ~(1ul << k); }
            _denseEpoch++;
            _joined[(kind, key)] = ((nint)routes, part);
            return routes;
        }
    }

    internal int RowOf(int entity, int kind)
    {
        var mask = _mask[entity];
        if ((mask & (1ul << kind)) == 0) return -1;
        return _rows[_offset[entity] + Pop(mask & ((1ul << kind) - 1))];
    }

    internal void ValidateStart(int entity, ushort timeline)
    {
        if (Unreachable(entity, timeline) is { } bad) throw bad;
    }

    internal ArgumentException? Unreachable(int entity, ushort timeline)
    {
        var missing = _closure[timeline - 1] & ~_mask[entity];
        return missing == 0 ? null : new ArgumentException($"Entity {entity} cannot reach kind '{_kindNames[Tz(missing)]}' fed by timeline {timeline} or its next chain; add the kind to the entity's reach.");
    }

    internal void Purge(int entity, int slot, in Playback playback)
    {
        if (playback.Timeline != 0) _kindRows.Purge(entity, (byte)slot, playback.Timeline);
    }

    internal void ResetEntity(int entity)
    {
        _samples[entity] = 0;
        var w = entity >> 6;
        var b = ~(1ul << (entity & 63));
        if (_closedEntity != null) _closedEntity[w] &= b;
        for (var k = 0; k < _kindCount; k++)
        {
            if (_pulseHits != null && _pulseHits[k].Count > 0) StripPulseHits(_pulseHits + k, entity);
            var row = RowOf(entity, k);
            if (row >= 0 && _tables[k] != null)
            {
                _tables[k]->Clear(row);
                _listed[k][row >> 6] &= ~(1ul << (row & 63));
            }
            var q = _queues + k;
            var kept = 0;
            for (var n = 0; n < q->Count; n++)
            {
                var at = q->Items + (nuint)n * (nuint)q->Stride;
                if (*(int*)at == entity) continue;
                if (kept != n) Buffer.MemoryCopy(at, q->Items + (nuint)kept * (nuint)q->Stride, q->Stride, q->Stride);
                kept++;
            }
            q->Count = kept;
        }
    }

    static void StripPulseHits(KindPulseBuffer* buf, int entity)
    {
        var kept = 0;
        var items = buf->Items;
        var count = buf->Count;
        for (var i = 0; i < count; i++)
        {
            if (items[i].Entity == entity) continue;
            if (kept != i) items[kept] = items[i];
            kept++;
        }
        buf->Count = kept;
    }

    internal void Close(int entity, int slot, ref Playback playback)
    {
        _players.SetPlaying(entity, true);
        if (_closedEntity != null) { _closedEntity[entity >> 6] |= 1ul << (entity & 63); _hasClosed = true; }
        KindRules.Close(in _t, ref _kindRows, entity, ref playback, slot);
    }

    internal struct Rows : IKindRows
    {
        public KindStore Store;
        public KindTable** Tables;
        public ulong* Mask;
        public int* Offset;
        public int* Row;

        [MethodImpl(Inline)]
        int RowOf(int entity, int kind) => Row[Offset[entity] + Pop(Mask[entity] & ((1ul << kind) - 1))];

        [MethodImpl(Inline)]
        public KindEntry* Append(int entity, LibraryFeeder* feeder)
        {
            var kind = feeder->Kind;
            var table = Tables[kind];
            var row = RowOf(entity, kind);
            if ((table->Enabled[row >> 6] >> (row & 63) & 1) == 0) return null;
            Store._slowPulseMask |= 1ul << kind;
            var entry = table->Append(row);
            entry->Feeder = feeder->Ordinal;
            entry->Owner = 0;
            if (table->Grouped) *(ushort*)((byte*)entry + 8) = (ushort)kind;
            return entry;
        }

        public byte* Samples;
        public int EntityCount;

        [MethodImpl(Inline)]
        public void Sampled(int entity, int samples)
        {
            if (samples != 0)
            {
                Samples[entity] = (byte)samples;
                Samples[EntityCount] = 1;
            }
        }

        public void Purge(int entity, byte slot, ushort timeline)
        {
            var kinds = Mask[entity];
            while (kinds != 0)
            {
                var kind = Tz(kinds);
                kinds &= kinds - 1;
                Tables[kind]->Purge(RowOf(entity, kind), slot, timeline);
            }
        }

        public PlaybackTable Players;

        public Playback* Reserve(int entity, int used, int length) => Players.Reserve(entity, length, used);
    }

    internal void Sync()
    {
        if (_generation != _world._generation) Rebuild();
    }

    static bool HasActive(KindTable* t)
    {
        if (t == null) return false;
        for (var w = 0; w < t->Words; w++)
            if (t->Active[w] != 0) return true;
        return false;
    }

    void FlushPulseHits(int kind)
    {
        var ph = _pulseHits + kind;
        var count = ph->Count;
        if (count == 0) return;
        var table = _tables[kind];
        var pv = _pulseValues + kind;
        var items = ph->Items;
        var vbytes = _valueBytes[kind];
        var stride = table->Stride;
        byte[]? saved = null;
        var i = 0;
        while (i < count)
        {
            var e = items[i].Entity;
            var j = i + 1;
            while (j < count && items[j].Entity == e) j++;
            if ((table->Enabled[e >> 6] >> (e & 63) & 1) != 0)
            {
                var existing = (int)table->Length[e];
                var existingBytes = existing * stride;
                if (existing != 0)
                {
                    if (saved == null || saved.Length < existingBytes) saved = new byte[existingBytes];
                    fixed (byte* sp = saved) Buffer.MemoryCopy(table->Data(e), sp, existingBytes, existingBytes);
                    table->Clear(e);
                }
                for (var h = i; h < j; h++)
                {
                    var cell = items[h].Cell;
                    for (var p = pv->Start[cell]; p < pv->Start[cell + 1]; p++)
                    {
                        var entry = table->Append(e);
                        *entry = new KindEntry { Flags = KindEntryFlags.Pulse };
                        CopyValue((byte*)entry + table->ValueAt, pv->Data + (nuint)p * (nuint)vbytes, vbytes);
                    }
                }
                if (existing != 0)
                    fixed (byte* sp = saved)
                        for (var r = 0; r < existing; r++)
                            Buffer.MemoryCopy(sp + r * stride, table->Append(e), stride, stride);
            }
            i = j;
        }
        ph->Count = 0;
    }

    internal void TickPass(bool forward, TlWorkers? workers)
    {
        Sync();
        var entities = _players.EntityCapacity;
        var words = _players.PlayingWords;
        if (_samples[entities] != 0) NativeMemory.Clear(_samples, (nuint)entities + 1);
        _t.EntryPulse = _hasClosed ? _closedEntity : null;
        var fast = _tablePulses;
        for (var m = fast | _tickTablePulses; m != 0; m &= m - 1)
        {
            var k = Tz(m);
            if (_pulseHits[k].Count != 0)
            {
                FlushPulseHits(k);
                _slowPulseMask |= 1ul << k;
                fast &= ~(1ul << k);
            }
            else if ((_slowPulseMask >> k & 1) != 0)
            {
                if (HasActive(_tables[k])) fast &= ~(1ul << k);
                else _slowPulseMask &= ~(1ul << k);
            }
        }
        _tickTablePulses = fast;
        _t.FastPulseMask = fast;
        DenseBegin(forward ? 1 : -1);
        if (_denseReady && workers?.WorkerCount > 1 && words > 1) Lanes(forward ? 1 : -1, words, workers);
        else TickPassRange(forward ? 1 : -1, 0, words);
        DenseEnd(forward ? 1 : -1);
        if (_hasClosed)
        {
            NativeMemory.Clear(_closedEntity, (nuint)words * 8);
            _hasClosed = false;
        }
        _t.EntryPulse = null;
        MergePulses();
    }

    internal void TickPassRange(int stepDir, int firstWord, int endWord)
    {
        var playing = _players.PlayingBits;
        if (firstWord < 0) firstWord = 0; if (endWord > _players.PlayingWords) endWord = _players.PlayingWords;
        if (_denseReady) { DenseRange(stepDir, firstWord, endWord, _pulseHits); return; }
        var p = _players.RawView;
        var slots = p.Slots;
        var counts = p.Counts;
        var spilled = p.Spilled;
        var stride = (nuint)p.SlotsPerEntity;
        var pulse = default(BufferPulseSink);
        for (var w = firstWord; w < endWord; w++)
        {
            var bits = playing[w];
            if (bits == 0) continue;
            var baseE = w << 6;
            while (bits != 0)
            {
                var bit = Tz(bits);
                bits &= bits - 1;
                var e = baseE + bit;
                var inline = slots + (nuint)e * stride;
                var run = (spilled[w] >> bit & 1) == 0 ? inline : _players.RawView.Blocks + *(int*)inline;
                if (!KindRules.TickEntity(in _t, run, counts[e], ref _kindRows, e, stepDir, ref pulse)) playing[w] &= ~(1ul << bit);
            }
        }
    }

    void MergePulses()
    {
        for (var k = 0; k < _kindCount; k++)
        {
            var q = _queues + k;
            if (q->Count == 0) continue;
            var table = _tables[k];
            var count = q->Count;
            q->Count = 0;
            for (var n = 0; n < count; n++)
            {
                var at = q->Items + (nuint)n * (nuint)q->Stride;
                var entity = *(int*)at;
                var row = table == null ? -1 : RowOf(entity, k);
                if (row < 0)
                {
                    DroppedPulses++;
                    continue;
                }
                if ((table->Enabled[row >> 6] >> (row & 63) & 1) == 0) continue;
                _slowPulseMask |= 1ul << k;
                var entry = table->Append(row);
                *entry = new KindEntry { Flags = KindEntryFlags.Pulse };
                if (table->Grouped) *(ushort*)((byte*)entry + 8) = (ushort)k;
                CopyValue((byte*)entry + table->ValueAt, at + 4, _valueBytes[k]);
            }
        }
    }

    void Rebuild()
    {
        var pending = new List<(string Name, int Entity, byte[] Value)>();
        for (var k = 0; k < _kindCount; k++)
            for (var n = 0; n < _queues[k].Count; n++)
            {
                var at = _queues[k].Items + (nuint)n * (nuint)_queues[k].Stride;
                var value = new byte[_valueBytes[k]];
                new ReadOnlySpan<byte>(at + 4, value.Length).CopyTo(value);
                pending.Add((_kindNames[k], *(int*)at, value));
            }
        Build();
        foreach (var (name, entity, value) in pending)
        {
            var k = IndexOf(name);
            if (k < 0 || _valueBytes[k] != value.Length) DroppedPulses++;
            else fixed (byte* v = value) Send(k, entity, v);
        }
        var p = _players;
        for (var pass = 0; pass < 2; pass++)
            for (var e = 0; e < p.EntityCapacity; e++)
            {
                var slots = p.SlotsOf(e);
                var count = p.CountOf(e);
                for (var i = 0; i < count; i++)
                {
                    var s = slots + i;
                    if (s->Timeline == 0) continue;
                    if (pass == 1)
                    {
                        if (KindRules.Rebind(in _t, ref _kindRows, e, ref *s, i)) p.SetPlaying(e, true);
                        continue;
                    }
                    if ((s->Flags & PlaybackFlags.Stopping) != 0) continue;
                    var bad = s->Timeline > _timelineCount ? new ArgumentException($"Playback slot {i} of entity {e} names no timeline in the reloaded library.") : Unreachable(e, s->Timeline);
                    if (bad == null) continue;
                    _generation = 0;
                    throw bad;
                }
            }
    }

    void Build()
    {
        Release();
        var view = _world._view;
        _generation = _world._generation;
        _sec = view.Kinds;
        _tls = view.Timelines;
        _timelineCount = view.TimelineCount;
        _kindCount = _sec.IsEmpty ? 0 : _sec.KindCount;
        _epoch++;
        _kindNames = new string[_kindCount];
        _valueBytes = new int[_kindCount];
        _accepts = new byte[_kindCount];
        _folds = new bool[_kindCount];
        _routes = new ushort*[_kindCount];
        _parts = new List<int>?[_kindCount];
        _kindIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var k = 0; k < _kindCount; k++)
        {
            _kindNames[k] = _sec.NameOf(k);
            _kindIndex[_kindNames[k]] = k;
            _valueBytes[k] = (int)_sec.Kinds[k].ValueBytes;
            _accepts[k] = _sec.Kinds[k].Accepts;
            _folds[k] = _sec.Kinds[k].Flags != 0;
        }
        var entities = _players.EntityCapacity;
        _owner = new int[_kindCount];
        var firstOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var k = 0; k < _kindCount; k++)
        {
            _owner[k] = k;
            if (_sec.Kinds[k].GroupNameLength == 0) continue;
            var group = _sec.GroupOf(k);
            if (firstOf.TryGetValue(group, out var first)) _owner[k] = first;
            else firstOf[group] = k;
        }
        _mask = (ulong*)Zeroed((nuint)entities * 8);
        var all = _kindCount == 64 ? ~0ul : (1ul << _kindCount) - 1;
        if (_names == null) for (var e = 0; e < entities; e++) _mask[e] = all;
        else
        {
            var map = new int[_names.Length];
            for (var n = 0; n < map.Length; n++) map[n] = IndexOf(_names[n]);
            for (var e = 0; e < entities; e++)
                for (var n = 0; n < map.Length; n++)
                    if (map[n] >= 0 && (_reach![e] & (1ul << n)) != 0) _mask[e] |= 1ul << map[n];
        }
        _offset = (int*)Zeroed((nuint)entities * 4);
        var refs = 0;
        for (var e = 0; e < entities; e++)
        {
            _offset[e] = refs;
            refs += Pop(_mask[e]);
        }
        _rows = (int*)Zeroed((nuint)Math.Max(1, refs) * 4);
        _tables = (KindTable**)Zeroed((nuint)Math.Max(1, _kindCount) * (nuint)sizeof(nint));
        _queues = (PulseQueue*)Zeroed((nuint)Math.Max(1, _kindCount) * (nuint)sizeof(PulseQueue));
        for (var o = 0; o < _kindCount; o++)
        {
            ref var row = ref _sec.Kinds[o];
            _queues[o].Stride = 4 + (int)((row.ValueBytes + 3u) & ~3u);
            if (_owner[o] != o) continue;
            var bits = 0ul;
            var largest = 0u;
            for (var k = o; k < _kindCount; k++)
                if (_owner[k] == o)
                {
                    bits |= 1ul << k;
                    largest = Math.Max(largest, _sec.Kinds[k].ValueBytes);
                }
            var count = 0;
            for (var e = 0; e < entities; e++)
                if ((_mask[e] & bits) != 0) count++;
            if (count == 0) continue;
            var members = (int*)Zeroed((nuint)count * 4);
            var member = 0;
            for (var e = 0; e < entities; e++)
            {
                if ((_mask[e] & bits) == 0) continue;
                members[member] = e;
                var found = _mask[e] & bits;
                while (found != 0)
                {
                    var k = Tz(found);
                    found &= found - 1;
                    _rows[_offset[e] + Pop(_mask[e] & ((1ul << k) - 1))] = member;
                }
                member++;
            }
            var grouped = row.GroupNameLength != 0;
            var table = KindTable.Create(o, new ReadOnlySpan<int>(members, count), grouped ? (int)row.GroupCapacity : row.Accepts == (byte)KindEntries.Spans ? 0 : (int)row.Capacity, grouped ? 12 + (int)((largest + 3u) & ~3u) : 8 + (int)((row.ValueBytes + 3u) & ~3u), grouped);
            NativeMemory.AlignedFree(members);
            for (var k = o; k < _kindCount; k++)
                if (_owner[k] == o) _tables[k] = table;
        }
        _closure = (ulong*)Zeroed((nuint)Math.Max(1, _timelineCount) * 8);
        _pairStart = (int*)Zeroed((nuint)(_timelineCount + 2) * 4);
        var pairs = new List<uint>();
        for (var t = 0; t < _timelineCount; t++)
        {
            var at = t;
            for (var hop = 0; hop <= _timelineCount; hop++)
            {
                if (_kindCount != 0) _closure[t] |= _sec.Timelines[at].KindMask;
                if (_tls[at].Next == 0) break;
                at = _tls[at].Next - 1;
            }
            _pairStart[t] = pairs.Count;
            if (_kindCount == 0 || _sec.Timelines[t].KindMask == 0) continue;
            var tk = _sec.Timelines + t;
            var events = _sec.Events(tk);
            var set = new SortedSet<uint>();
            for (var n = 0u; n < tk->EventCount; n++)
                if (events[n].Op != (byte)KindEventOp.Remove) set.Add(((uint)events[n].Window << 16) | events[n].Feeder);
            pairs.AddRange(set);
        }
        _pairStart[_timelineCount] = pairs.Count;
        BuildSteps();
        _pairs = (uint*)Zeroed((nuint)Math.Max(1, pairs.Count) * 4);
        for (var n = 0; n < pairs.Count; n++) _pairs[n] = pairs[n];
        _spanMeta = (uint*)Zeroed((nuint)Math.Max(1, _timelineCount) * 4);
        _pulseBase = (int*)Zeroed((nuint)Math.Max(1, _timelineCount) * 4);
        var pulseTicks = 0;
        for (var t = 0; t < _timelineCount; t++)
        {
            var dur = _tls[t].Duration;
            _spanMeta[t] = dur > 0 && dur <= 65536u ? (dur | (_tls[t].Loop != 0 ? 0x8000_0000u : 0u)) : 0u;
            _pulseBase[t] = -1;
            if (_kindCount == 0 || _sec.Timelines[t].KindMask == 0 || dur == 0) continue;
            var tk = _sec.Timelines + t;
            var events = _sec.Events(tk);
            for (var n = 0u; n < tk->EventCount; n++)
                if (events[n].Op >= (byte)KindEventOp.PulseStart) { _pulseBase[t] = pulseTicks; pulseTicks += (int)dur; break; }
        }
        _pulseBits = (byte*)Zeroed((nuint)Math.Max(1, pulseTicks));
        _pulseMasks = (ulong*)Zeroed((nuint)Math.Max(1, pulseTicks) * 16);
        if (_kindCount != 0)
        {
            var feeders = _sec.Feeders;
            for (var t = 0; t < _timelineCount; t++)
            {
                var baseAt = _pulseBase[t];
                if (baseAt < 0) continue;
                var tk = _sec.Timelines + t;
                var events = _sec.Events(tk);
                var dur = (int)_tls[t].Duration;
                for (var tick = 0; tick < dur; tick++)
                {
                    var bits = _sec.EventBitsAt(tk, tick);
                    byte pb = 0;
                    for (var d = 0; d < 2; d++)
                    {
                        if ((bits & (1 << d)) == 0) continue;
                        ulong km = 0;
                        var to = _sec.EventEnd(tk, tick, d);
                        for (var n = _sec.EventStart(tk, tick, d); n < to; n++)
                        {
                            var ev = events[n];
                            if (ev.Op >= (byte)KindEventOp.PulseStart) km |= 1ul << feeders[ev.Feeder].Kind;
                        }
                        if (km != 0)
                        {
                            pb |= (byte)(1 << d);
                            _pulseMasks[((baseAt + tick) << 1) + d] = km;
                        }
                    }
                    _pulseBits[baseAt + tick] = pb;
                }
            }
        }
        BuildRows();
        _samples = (byte*)Zeroed((nuint)entities + 1);
        _listed = (ulong**)Zeroed((nuint)Math.Max(1, _kindCount) * (nuint)sizeof(nint));
        for (var k = 0; k < _kindCount; k++) _listed[k] = (ulong*)Zeroed((nuint)(_tables[k] == null ? 1 : _tables[k]->Words) * 8);
        _values = (KindValues*)Zeroed((nuint)Math.Max(1, _kindCount) * (nuint)sizeof(KindValues));
        _pulseHits = (KindPulseBuffer*)Zeroed((nuint)Math.Max(1, _kindCount) * (nuint)sizeof(KindPulseBuffer));
        _pulseValues = (KindPulseValues*)Zeroed((nuint)Math.Max(1, _kindCount) * (nuint)sizeof(KindPulseValues));
        var words = _players.PlayingWords;
        _closedEntity = (ulong*)Zeroed((nuint)words * 8);
        _t = new KindTables
        {
            Timelines = _tls,
            TimelineCount = _timelineCount,
            Sec = _sec,
            KindCount = _kindCount,
            Closure = _closure,
            Pairs = _pairs,
            PairStart = _pairStart,
            SpanMeta = _spanMeta,
            PulseBase = _pulseBase,
            PulseBits = _pulseBits,
            PulseMasks = _pulseMasks,
            PulseHits = _pulseHits,
        };
        var steps = new Steps { Store = this };
        _score = KindScore.Build(in _t, ref steps);
        _kindRows = new Rows { Store = this, Tables = _tables, Mask = _mask, Offset = _offset, Row = _rows, Players = _players, Samples = _samples, EntityCount = entities };
    }

    void BuildSteps()
    {
        _windowBase = (int*)Zeroed((nuint)(_timelineCount + 1) * 4);
        var total = 0;
        for (var t = 0; t < _timelineCount; t++)
        {
            _windowBase[t] = total;
            if (_kindCount != 0 && _sec.Timelines[t].KindMask != 0) total += (int)_sec.Timelines[t].WindowCount;
        }
        _windowBase[_timelineCount] = total;
        _windowTotal = total;
        _windows = (WindowSteps*)Zeroed((nuint)Math.Max(1, total) * (nuint)sizeof(WindowSteps));
        for (var t = 0; t < _timelineCount; t++)
        {
            if (_kindCount == 0 || _sec.Timelines[t].KindMask == 0) continue;
            var tk = _sec.Timelines + t;
            var tl = _tls + t;
            var windows = _sec.Windows(tk);
            for (var w = 0; w < tk->WindowCount; w++)
            {
                var win = windows[w];
                if ((win.Flags & 1u) != 0) continue;
                int first = -1, last = -1;
                for (var s = 0; s < tl->StageCount; s++)
                {
                    if (tl->Stages[s].Start <= win.Start && win.Start < tl->Stages[s].End) first = s;
                    if (tl->Stages[s].Start <= win.End - 1 && win.End - 1 < tl->Stages[s].End) last = s;
                }
                if (first < 0 || last < first) continue;
                var arr = (LibraryStep**)Zeroed((nuint)(last - first + 1) * (nuint)sizeof(nint));
                for (var s = first; s <= last; s++)
                    for (var n = 0; n < tl->Stages[s].StepCount; n++)
                    {
                        var step = tl->Stages[s].Steps + n;
                        if (step->TrackIndex == win.Track && step->Pair == win.Pair && step->WindowStart == win.Start && step->WindowEnd == win.End) { arr[s - first] = step; break; }
                    }
                var g = _windowBase[t] + w;
                _windows[g] = new WindowSteps(arr, first, last - first + 1);
            }
        }
    }

    static void CopyValue(byte* to, byte* from, int bytes)
    {
        if (bytes == 4) *(int*)to = *(int*)from;
        else if (bytes == 8) *(long*)to = *(long*)from;
        else Buffer.MemoryCopy(from, to, bytes, bytes);
    }

    internal static void* Zeroed(nuint bytes)
    {
        var p = NativeMemory.AlignedAlloc(bytes == 0 ? 1 : bytes, 64);
        NativeMemory.Clear(p, bytes);
        return p;
    }

    void Release()
    {
        UndenseAll();
        ReleaseRows();
        if (_tables != null)
            for (var k = 0; k < _kindCount; k++) if (_owner[k] == k) KindTable.Free(_tables[k]);
        if (_queues != null)
            for (var k = 0; k < _kindCount; k++) NativeMemory.AlignedFree(_queues[k].Items);
        if (_windows != null)
            for (var g = 0; g < _windowTotal; g++) NativeMemory.AlignedFree(_windows[g].Steps);
        _joined.Clear();
        _feeding.Clear();
        for (var r = 0; r < _routes.Length; r++) NativeMemory.AlignedFree(_routes[r]);
        _routes = new ushort*[0];
        _parts = Array.Empty<List<int>?>();
        _score.Free();
        if (_listed != null)
            for (var k = 0; k < _kindCount; k++) NativeMemory.AlignedFree(_listed[k]);
        if (_values != null)
            for (var k = 0; k < _kindCount; k++) _values[k].Free();
        if (_pulseHits != null)
            for (var k = 0; k < _kindCount; k++) _pulseHits[k].Free();
        if (_pulseValues != null)
            for (var k = 0; k < _kindCount; k++) _pulseValues[k].Free();
        Free4(_tables, _queues, _mask, _offset);
        Free4(_rows, _closure, _pairs, _pairStart);
        Free4(_windowBase, _windows, _samples, _listed);
        Free4(_values, _spanMeta, _pulseBase, _pulseBits);
        Free4(_pulseMasks, _pulseHits, _pulseValues, _closedEntity);
        _samples = _pulseBits = null;
        _pulseMasks = _closedEntity = _mask = _closure = null;
        _offset = _rows = _pairStart = _windowBase = _pulseBase = null;
        _pairs = _spanMeta = null;
        _listed = null;
        _tables = null;
        _queues = null;
        _values = null;
        _pulseHits = null;
        _pulseValues = null;
        _windows = null;
        _tablePulses = _tickTablePulses = _slowPulseMask = 0;
        _hasClosed = false;
        _windowTotal = 0;
    }

    internal static void Free4(void* a, void* b, void* c, void* d)
    {
        NativeMemory.AlignedFree(a);
        NativeMemory.AlignedFree(b);
        NativeMemory.AlignedFree(c);
        NativeMemory.AlignedFree(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _players.AttachKinds(null);
        GC.SuppressFinalize(this);
    }

    internal static int Pop(ulong v)
    {
#if NET
        return System.Numerics.BitOperations.PopCount(v);
#else
        v -= (v >> 1) & 0x5555555555555555ul;
        v = (v & 0x3333333333333333ul) + ((v >> 2) & 0x3333333333333333ul);
        return (int)((((v + (v >> 4)) & 0x0F0F0F0F0F0F0F0Ful) * 0x0101010101010101ul) >> 56);
#endif
    }

    internal static int Tz(ulong v)
    {
#if NET
        return System.Numerics.BitOperations.TrailingZeroCount(v);
#else
        return Pop((v & (ulong)-(long)v) - 1);
#endif
    }
}
