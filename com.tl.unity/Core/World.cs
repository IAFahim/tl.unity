using System;

namespace Tl;

public readonly struct TickReport
{
    public TickReport(int invalidRequests, int highWaterVisits, int activeCount, int frame)
    {
        InvalidRequests = invalidRequests;
        HighWaterVisits = highWaterVisits;
        ActiveCount = activeCount;
        Frame = frame;
    }

    public readonly int InvalidRequests;
    public readonly int HighWaterVisits;
    public readonly int ActiveCount;
    public readonly int Frame;
}

public sealed unsafe class World : IDisposable
{
    internal sealed class EventHolder<T> where T : unmanaged
    {
        public T[] Data = new T[64];
        public int Count;

        public void Raise(T value)
        {
            if (Count == Data.Length)
            {
                var grown = new T[Data.Length * 2];
                Array.Copy(Data, grown, Data.Length);
                Data = grown;
            }
            Data[Count++] = value;
        }

        public Span<T> Drain()
        {
            var span = new Span<T>(Data, 0, Count);
            Count = 0;
            return span;
        }
    }

    public readonly struct WorldEvents<T> where T : unmanaged
    {
        private readonly EventHolder<T>? _holder;

        internal WorldEvents(EventHolder<T> holder)
        {
            _holder = holder;
        }

        public int Count => _holder == null ? 0 : _holder.Count;

        public void Raise(in T value)
        {
            if (_holder != null) _holder.Raise(value);
        }

        public Span<T> Drain()
        {
            if (_holder == null) return default;
            return _holder.Drain();
        }
    }

    public sealed class RequestApi
    {
        private readonly World _world;

        internal RequestApi(World world)
        {
            _world = world;
        }

        public void Start(Row row, TimelineId timeline) => _world.StartPlayback(row, timeline, 0);

        public void Start(Row row, TimelineId timeline, int phase) => _world.StartPlayback(row, timeline, phase);

        public void Stop(Row row, TimelineId timeline) => _world.StopPlayback(row, timeline);
    }

    private readonly Library _library;
    private readonly int _rowCapacity;
    private readonly float[] _acc;
    private readonly float[] _applied;
    private readonly int[] _duration;
    private readonly int[] _stepCount;
    private readonly int[] _stepFlatBase;
    private readonly int[] _stepGlobal;
    private readonly int[] _stepWindowStart;
    private readonly int[] _stepWindowEnd;
    private readonly int[] _stepKind;
    private readonly int[] _stepKindOrder;
    private int[] _pbRow;
    private ushort[] _pbTimeline;
    private ushort[] _pbPhase;
    private int[] _workRow;
    private ushort[] _workTimeline;
    private ushort[] _workPhase;
    private int[] _order;
    private int[] _scratch;
    private uint[] _radixKeys;
    private int[] _visitRow;
    private int[] _visitTick;
    private int[] _visitTimeline;
    private int[] _visitStep;
    private int[] _visitKindId;
    private int[] _visitKindOrder;
    private int[] _visitFlat;
    private int[] _visitSubstep;
    private int[] _groupRows;
    private object?[] _eventHolders;
    private int _frame;
    private int _invalidPending;
    private int _highWater;
    private int _playbackCount;
    private int _visitCount;
    private TickReport _report;
    private bool _sortedDirty = true;
    private bool _singleKind = true;

    public World(Library library, int rowCapacity)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        if (rowCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(rowCapacity));
        _rowCapacity = rowCapacity;
        _acc = new float[rowCapacity];
        _applied = new float[rowCapacity];
        var timelineCount = _library.TimelineCount;
        _duration = new int[timelineCount];
        _stepCount = new int[timelineCount];
        _stepFlatBase = new int[timelineCount];
        var totalSteps = 0;
        for (var i = 0; i < timelineCount; i++)
        {
            var row = _library.Timeline(i);
            var d = (int)row.Duration;
            _duration[i] = d < 0 ? 0 : d;
            var s = (int)row.StepCount;
            _stepCount[i] = s < 0 ? 0 : s;
            _stepFlatBase[i] = totalSteps;
            totalSteps += _stepCount[i];
        }
        _stepGlobal = new int[totalSteps];
        _stepWindowStart = new int[totalSteps];
        _stepWindowEnd = new int[totalSteps];
        _stepKind = new int[totalSteps];
        _stepKindOrder = new int[totalSteps];
        for (var i = 0; i < timelineCount; i++)
        {
            var globalBase = (int)_library.Timeline(i).StepIndex;
            for (var s = 0; s < _stepCount[i]; s++)
            {
                var flat = _stepFlatBase[i] + s;
                var g = globalBase + s;
                var step = _library.Step(g);
                _stepGlobal[flat] = g;
                _stepWindowStart[flat] = (int)step.WindowStart;
                _stepWindowEnd[flat] = (int)step.WindowEnd;
                _stepKind[flat] = (int)step.KindId;
                _stepKindOrder[flat] = KindOrderOf((int)step.KindId);
            }
        }
        var cap = rowCapacity < 64 ? 64 : rowCapacity;
        _pbRow = new int[cap];
        _pbTimeline = new ushort[cap];
        _pbPhase = new ushort[cap];
        _workRow = new int[cap];
        _workTimeline = new ushort[cap];
        _workPhase = new ushort[cap];
        _order = new int[cap];
        _scratch = new int[cap];
        _radixKeys = new uint[cap];
        _visitRow = new int[cap];
        _visitTick = new int[cap];
        _visitTimeline = new int[cap];
        _visitStep = new int[cap];
        _visitKindId = new int[cap];
        _visitKindOrder = new int[cap];
        _visitFlat = new int[cap];
        _visitSubstep = new int[cap];
        _groupRows = new int[cap];
        _eventHolders = new object?[4];
        _report = new TickReport(0, 0, 0, 0);
        if (_stepKind.Length > 0)
        {
            var firstKind = _stepKind[0];
            for (var i = 1; i < _stepKind.Length; i++)
            {
                if (_stepKind[i] != firstKind)
                {
                    _singleKind = false;
                    break;
                }
            }
        }
    }

    public int RowCapacity => _rowCapacity;

    public int Frame => _frame;

    public int ActiveCount => _playbackCount;

    public float[] Accumulator => _applied;

    public TickReport Report => _report;

    public RequestApi Requests
    {
        get { return new RequestApi(this); }
    }

    public WorldEvents<T> Events<T>() where T : unmanaged
    {
        for (var i = 0; i < _eventHolders.Length; i++)
            if (_eventHolders[i] is EventHolder<T> h) return new WorldEvents<T>(h);
        var holder = new EventHolder<T>();
        for (var i = 0; i < _eventHolders.Length; i++)
        {
            if (_eventHolders[i] != null) continue;
            _eventHolders[i] = holder;
            return new WorldEvents<T>(holder);
        }
        var old = _eventHolders.Length;
        Array.Resize(ref _eventHolders, old * 2);
        _eventHolders[old] = holder;
        return new WorldEvents<T>(holder);
    }

    public void Tick(Direction direction)
    {
        _frame++;
        var dir = direction == Direction.Reverse ? -1 : 1;
        var invalid = _invalidPending;
        _invalidPending = 0;
        var n = _playbackCount;
        for (var i = 0; i < n; i++)
        {
            _workRow[i] = _pbRow[i];
            _workTimeline[i] = _pbTimeline[i];
            _workPhase[i] = _pbPhase[i];
        }
        if (_sortedDirty)
        {
            for (var i = 0; i < n; i++) _order[i] = i;
            for (var i = 0; i < n; i++) _radixKeys[i] = _workPhase[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            for (var i = 0; i < n; i++) _radixKeys[i] = _workTimeline[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            _sortedDirty = false;
        }
        _visitCount = 0;
        var runStart = 0;
        while (runStart < n)
        {
            var firstWork = _order[runStart];
            var timelineValue = _workTimeline[firstWork];
            var phase = _workPhase[firstWork];
            var runEnd = runStart + 1;
            while (runEnd < n)
            {
                var w = _order[runEnd];
                if (_workTimeline[w] != timelineValue || _workPhase[w] != phase) break;
                runEnd++;
            }
            var duration = _duration[timelineValue - 1];
            if (duration > 0)
            {
                var tick = Kernels.PhaseToTick(phase, _frame, duration, dir);
                var baseFlat = _stepFlatBase[timelineValue - 1];
                var stepCount = _stepCount[timelineValue - 1];
                EnsureVisitCapacity(_visitCount + (runEnd - runStart) * stepCount);
                for (var s = 0; s < stepCount; s++)
                {
                    var flat = baseFlat + s;
                    if (tick < _stepWindowStart[flat] || tick >= _stepWindowEnd[flat]) continue;
                    var kindId = _stepKind[flat];
                    for (var m = runStart; m < runEnd; m++)
                    {
                        var v = _visitCount++;
                        _visitRow[v] = _workRow[_order[m]];
                        _visitTick[v] = tick;
                        _visitTimeline[v] = timelineValue;
                        _visitStep[v] = s;
                        _visitKindId[v] = kindId;
                        _visitKindOrder[v] = _stepKindOrder[flat];
                        _visitFlat[v] = flat;
                        _visitSubstep[v] = 0;
                    }
                }
            }
            runStart = runEnd;
        }
        var vc = _visitCount;
        if (vc > 1 && !_singleKind)
        {
            for (var i = 0; i < vc; i++) _order[i] = i;
            for (var i = 0; i < vc; i++) _radixKeys[i] = (uint)_visitRow[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            for (var i = 0; i < vc; i++) _radixKeys[i] = (uint)_visitStep[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            for (var i = 0; i < vc; i++) _radixKeys[i] = (uint)_visitTimeline[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            for (var i = 0; i < vc; i++) _radixKeys[i] = (uint)_visitKindId[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            for (var i = 0; i < vc; i++) _radixKeys[i] = (uint)_visitKindOrder[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
            for (var i = 0; i < vc; i++) _radixKeys[i] = (uint)_visitSubstep[i];
            Kernels.StableRadix(_radixKeys, _order, _scratch);
        }
        var laneDir = dir < 0 ? LaneDirection.Backward : LaneDirection.Forward;
        var v0 = 0;
        while (v0 < vc)
        {
            var first = _order[v0];
            var kindId = _visitKindId[first];
            var timelineValue = _visitTimeline[first];
            var step = _visitStep[first];
            var flat = _visitFlat[first];
            var w = v0 + 1;
            while (w < vc)
            {
                var id = _order[w];
                if (_visitKindId[id] != kindId || _visitTimeline[id] != timelineValue || _visitStep[id] != step) break;
                w++;
            }
            var globalStep = _stepGlobal[flat];
            var tick0 = _visitTick[first];
            var singleTick = true;
            for (var k = v0 + 1; k < w; k++)
            {
                if (_visitTick[_order[k]] != tick0)
                {
                    singleTick = false;
                    break;
                }
            }
            if (singleTick)
            {
                AccumulateBroadcast(v0, w, _library.LaneValue(globalStep, laneDir, tick0));
            }
            else
            {
                for (var k = v0; k < w; k++)
                {
                    var id = _order[k];
                    _acc[_visitRow[id]] += _library.LaneValue(globalStep, laneDir, _visitTick[id]);
                }
            }
            v0 = w;
        }
        Array.Copy(_acc, _applied, _rowCapacity);
        if (vc > _highWater) _highWater = vc;
        _report = new TickReport(invalid, _highWater, _playbackCount, _frame);
    }

    public void Dispose()
    {
    }

    private int KindOrderOf(int kindId)
    {
        var count = _library.KindCount;
        for (var k = 0; k < count; k++)
        {
            var row = _library.Kind(k);
            if ((int)row.Id == kindId) return (int)row.Order;
        }
        return kindId;
    }

    private void AccumulateBroadcast(int v, int w, float value)
    {
        var firstRow = _visitRow[_order[v]];
        var dense = true;
        for (var k = v; k < w; k++)
        {
            if (_visitRow[_order[k]] != firstRow + (k - v))
            {
                dense = false;
                break;
            }
        }
        if (dense)
        {
            Kernels.BroadcastDense(_acc, firstRow, value, w - v);
        }
        else
        {
            for (var k = v; k < w; k++) _groupRows[k - v] = _visitRow[_order[k]];
            Kernels.BroadcastSparse(_acc, 0, _groupRows, 0, w - v, value);
        }
    }

    private void StartPlayback(Row row, TimelineId timeline, int phase)
    {
        if (row.Value < 0 || row.Value >= _rowCapacity || timeline.Value == 0 || timeline.Value > _duration.Length || _duration[timeline.Value - 1] <= 0)
        {
            _invalidPending++;
            return;
        }
        EnsurePlaybackCapacity(_playbackCount + 1);
        _pbRow[_playbackCount] = row.Value;
        _pbTimeline[_playbackCount] = timeline.Value;
        _pbPhase[_playbackCount] = (ushort)(phase & 65535);
        _playbackCount++;
        _sortedDirty = true;
    }

    private void StopPlayback(Row row, TimelineId timeline)
    {
        var i = 0;
        while (i < _playbackCount)
        {
            if (_pbRow[i] == row.Value && _pbTimeline[i] == timeline.Value)
            {
                var last = --_playbackCount;
                _pbRow[i] = _pbRow[last];
                _pbTimeline[i] = _pbTimeline[last];
                _pbPhase[i] = _pbPhase[last];
                continue;
            }
            i++;
        }
    }

    private void EnsurePlaybackCapacity(int needed)
    {
        if (_pbRow.Length >= needed) return;
        var n = Math.Max(needed, _pbRow.Length * 2);
        Array.Resize(ref _pbRow, n);
        Array.Resize(ref _pbTimeline, n);
        Array.Resize(ref _pbPhase, n);
        Array.Resize(ref _workRow, n);
        Array.Resize(ref _workTimeline, n);
        Array.Resize(ref _workPhase, n);
        EnsureSortCapacity(n);
    }

    private void EnsureVisitCapacity(int needed)
    {
        if (_visitRow.Length >= needed) return;
        var n = Math.Max(needed, _visitRow.Length * 2);
        Array.Resize(ref _visitRow, n);
        Array.Resize(ref _visitTick, n);
        Array.Resize(ref _visitTimeline, n);
        Array.Resize(ref _visitStep, n);
        Array.Resize(ref _visitKindId, n);
        Array.Resize(ref _visitKindOrder, n);
        Array.Resize(ref _visitFlat, n);
        Array.Resize(ref _visitSubstep, n);
        Array.Resize(ref _groupRows, n);
        EnsureSortCapacity(n);
    }

    private void EnsureSortCapacity(int needed)
    {
        if (_order.Length >= needed) return;
        var n = Math.Max(needed, _order.Length * 2);
        Array.Resize(ref _order, n);
        Array.Resize(ref _scratch, n);
        Array.Resize(ref _radixKeys, n);
    }
}
