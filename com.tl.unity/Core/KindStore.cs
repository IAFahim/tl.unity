using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

public readonly struct KindStats
{
    public KindStats(int rows, int maxLength, int overflows, long bytesPerRow)
    {
        Rows = rows;
        MaxLength = maxLength;
        Overflows = overflows;
        BytesPerRow = bytesPerRow;
    }

    public readonly int Rows;
    public readonly int MaxLength;
    public readonly int Overflows;
    public readonly long BytesPerRow;
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

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PulseQueue
{
    public byte* Items;
    public int Count;
    public int Capacity;
    public int Stride;
}

public sealed unsafe class KindStore : IDisposable
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
    internal readonly unsafe struct WindowSteps
    {
        public readonly LibraryStep** Steps;
        public readonly int FirstStage;
        public readonly int StageCount;

        public WindowSteps(LibraryStep** steps, int firstStage, int stageCount)
        {
            Steps = steps;
            FirstStage = firstStage;
            StageCount = stageCount;
        }
    }

    WindowSteps* _windows;
    int _windowTotal;
    KindTables _t;
    Rows _kindRows;
    readonly Dictionary<(string, int), (nint Routes, int Part)> _joined = new();
    readonly HashSet<string> _feeding = new(StringComparer.Ordinal);
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
        return t == null ? default : new KindStats(t->Rows, t->MaxLength, t->Overflows, t->StorageBytesPerRow);
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
            if (q->Items != null) NativeMemory.AlignedFree(q->Items);
            q->Items = items;
            q->Capacity = capacity;
        }
        var at = q->Items + (nuint)q->Count * (nuint)q->Stride;
        *(int*)at = entity;
        CopyValue(at + 4, (byte*)value, _valueBytes[kind]);
        q->Count++;
    }

    public KindTable* Table(int kind) => (uint)kind < (uint)_kindCount ? _tables[kind] : null;

    public static int TrailingZeros(ulong value) => Tz(value);

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
        if (key != 0) _feeding.Add(kind);
        _joined[(kind, key)] = ((nint)routes, part);
        return routes;
    }

    internal int RowOf(int entity, int kind)
    {
        var mask = _mask[entity];
        if ((mask & (1ul << kind)) == 0) return -1;
        return _rows[_offset[entity] + Pop(mask & ((1ul << kind) - 1))];
    }

    internal void ClearPulses()
    {
        for (var k = 0; k < _kindCount; k++)
        {
            _queues[k].Count = 0;
            var t = _tables[k];
            if (t == null || _owner[k] != k) continue;
            for (var w = 0; w < t->Words; w++)
            {
                var bits = t->Active[w];
                while (bits != 0)
                {
                    t->RemovePulses((w << 6) + Tz(bits));
                    bits &= bits - 1;
                }
            }
        }
    }

    internal void ValidateStart(int entity, ushort timeline)
    {
        var missing = _closure[timeline - 1] & ~_mask[entity];
        if (missing == 0) return;
        throw new ArgumentException($"Entity {entity} cannot reach kind '{_kindNames[Tz(missing)]}' fed by timeline {timeline} or its next chain; add the kind to the entity's reach.");
    }

    internal void Purge(int entity, int slot, in Playback playback)
    {
        if (playback.Timeline != 0) _kindRows.Purge(entity, (byte)slot, playback.Timeline);
    }

    internal void Close(int entity, int slot, ref Playback playback)
    {
        _players.SetPlaying(entity, true);
        KindRules.Close(in _t, ref _kindRows, entity, ref playback, slot);
    }

    internal struct Rows : IKindRows
    {
        public KindTable** Tables;
        public ulong* Mask;
        public int* Offset;
        public int* Row;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int RowOf(int entity, int kind) => Row[Offset[entity] + Pop(Mask[entity] & ((1ul << kind) - 1))];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public KindEntry* Append(int entity, LibraryFeeder* feeder)
        {
            var kind = feeder->Kind;
            var table = Tables[kind];
            var entry = table->Append(RowOf(entity, kind));
            entry->Feeder = feeder->Ordinal;
            entry->Owner = 0;
            if (table->Grouped) *(ushort*)((byte*)entry + 8) = (ushort)kind;
            return entry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveSpan(int entity, int kind, byte slot, ushort timeline, ushort step) => Tables[kind]->RemoveSpan(RowOf(entity, kind), slot, timeline, step, kind);

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

        public Playback* Reserve(int entity, int used, int length) => Players.Reserve(entity, length);

        public void Sample(int entity, byte slot, byte sample)
        {
            var kinds = Mask[entity];
            while (kinds != 0)
            {
                var kind = Tz(kinds);
                kinds &= kinds - 1;
                Tables[kind]->Sample(RowOf(entity, kind), slot, sample);
            }
        }

        public void Unsample(int entity)
        {
            var kinds = Mask[entity];
            while (kinds != 0)
            {
                var kind = Tz(kinds);
                kinds &= kinds - 1;
                Tables[kind]->Unsample(RowOf(entity, kind));
            }
        }
    }

    internal void Sync()
    {
        if (_generation != _world._generation) Rebuild();
    }

    internal void TickPass(bool forward)
    {
        Sync();
        var stepDir = forward ? 1 : -1;
        var playing = _players.Playing;
        for (var w = 0; w < _players.PlayingWords; w++)
        {
            var bits = playing[w];
            while (bits != 0)
            {
                var e = (w << 6) + Tz(bits);
                bits &= bits - 1;
                TickEntity(e, stepDir);
            }
        }
        MergePulses();
    }

    void TickEntity(int e, int stepDir)
    {
        if (!KindRules.TickEntity(in _t, _players.SlotsOf(e), _players.CountOf(e), ref _kindRows, e, stepDir)) _players.SetPlaying(e, false);
    }

    void MergePulses()
    {
        for (var k = 0; k < _kindCount; k++)
        {
            var q = _queues + k;
            if (q->Count == 0) continue;
            var table = _tables[k];
            for (var n = 0; n < q->Count; n++)
            {
                var at = q->Items + (nuint)n * (nuint)q->Stride;
                var entity = *(int*)at;
                var row = table == null ? -1 : RowOf(entity, k);
                if (row < 0)
                {
                    DroppedPulses++;
                    continue;
                }
                var entry = table->Append(row);
                *entry = new KindEntry { Flags = KindEntryFlags.Pulse };
                if (table->Grouped) *(ushort*)((byte*)entry + 8) = (ushort)k;
                CopyValue((byte*)entry + table->ValueAt, at + 4, _valueBytes[k]);
            }
            q->Count = 0;
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
        for (var e = 0; e < p.EntityCapacity; e++)
        {
            var slots = p.SlotsOf(e);
            var count = p.CountOf(e);
            for (var i = 0; i < count; i++)
            {
                var s = slots + i;
                if (s->Timeline == 0) continue;
                if ((s->Flags & PlaybackFlags.Stopping) == 0)
                {
                    if (s->Timeline > _timelineCount) throw new ArgumentException($"Playback slot {i} of entity {e} names no timeline in the reloaded library.");
                    ValidateStart(e, s->Timeline);
                }
                if (KindRules.Rebind(in _t, ref _kindRows, e, ref *s, i)) p.SetPlaying(e, true);
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
            var table = KindTable.Create(o, new ReadOnlySpan<int>(members, count), (int)(grouped ? row.GroupCapacity : row.Capacity), grouped ? 12 + (int)((largest + 3u) & ~3u) : 8 + (int)((row.ValueBytes + 3u) & ~3u), grouped);
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
        _t = new KindTables { Timelines = _tls, TimelineCount = _timelineCount, Sec = _sec, KindCount = _kindCount, Closure = _closure, Pairs = _pairs, PairStart = _pairStart };
        _kindRows = new Rows { Tables = _tables, Mask = _mask, Offset = _offset, Row = _rows, Players = _players };
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

    static void* Zeroed(nuint bytes)
    {
        var p = NativeMemory.AlignedAlloc(bytes == 0 ? 1 : bytes, 64);
        NativeMemory.Clear(p, bytes);
        return p;
    }

    void Release()
    {
        if (_tables != null)
            for (var k = 0; k < _kindCount; k++) if (_owner[k] == k) KindTable.Free(_tables[k]);
        if (_queues != null)
            for (var k = 0; k < _kindCount; k++) if (_queues[k].Items != null) NativeMemory.AlignedFree(_queues[k].Items);
        if (_windows != null)
            for (var g = 0; g < _windowTotal; g++) if (_windows[g].Steps != null) NativeMemory.AlignedFree(_windows[g].Steps);
        _joined.Clear();
        _feeding.Clear();
        for (var r = 0; r < _routes.Length; r++) if (_routes[r] != null) NativeMemory.AlignedFree(_routes[r]);
        _routes = new ushort*[0];
        _parts = Array.Empty<List<int>?>();
        foreach (var block in new void*[] { _tables, _queues, _mask, _offset, _rows, _closure, _pairs, _pairStart, _windowBase, _windows })
            if (block != null) NativeMemory.AlignedFree(block);
        _tables = null;
        _queues = null;
        _mask = null;
        _offset = null;
        _rows = null;
        _closure = null;
        _pairs = null;
        _pairStart = null;
        _windowBase = null;
        _windows = null;
        _windowTotal = 0;
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
