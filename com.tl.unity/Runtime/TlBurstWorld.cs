using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Tl.Unity
{
    public static class ParallelApplyBurst
    {
        public static int ChunkCount(int total, int workers)
        {
            if (total <= 0) return 0;
            var count = workers <= 0 ? Environment.ProcessorCount : workers;
            if (count < 1) count = 1;
            return count > total ? total : count;
        }

        public static int ChunkSize(int total, int workers)
        {
            if (total <= 0) return 1;
            var count = ChunkCount(total, workers);
            var baseSize = total / count;
            return total % count == 0 ? baseSize : baseSize + 1;
        }

        public static void Partition(int total, int workers, int chunk, out int start, out int end)
        {
            var count = ChunkCount(total, workers);
            var baseSize = total / count;
            var extra = total % count;
            start = chunk * baseSize + (chunk < extra ? chunk : extra);
            end = start + baseSize + (chunk < extra ? 1 : 0);
        }
    }

    public sealed class BurstWorld : IDisposable
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RunDescriptor
        {
            public ushort Timeline;
            public ushort Phase;
            public int Duration;
            public int StepFlatBase;
            public int StepCount;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct PlanEntry
        {
            public int Run;
            public int StepLocal;
            public ushort Phase;
            public int Duration;
            public int Flat;
        }

        [BurstCompile(FloatMode = FloatMode.Strict)]
        struct RunValuesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RunDescriptor> Runs;
            [ReadOnly] public NativeArray<int> ForwardOffset;
            [ReadOnly] public NativeArray<int> ForwardCount;
            [ReadOnly] public NativeArray<int> BackwardOffset;
            [ReadOnly] public NativeArray<int> BackwardCount;
            [ReadOnly] public NativeArray<float> ForwardValues;
            [ReadOnly] public NativeArray<float> BackwardValues;
            [ReadOnly] public NativeArray<int> WindowStart;
            [ReadOnly] public NativeArray<int> WindowEnd;
[NativeDisableContainerSafetyRestriction] public NativeArray<float> Values;
            public int Ticks;
            public int FrameBase;
            public int Direction;
            public int MaxSteps;

            public void Execute(int index)
            {
                var run = Runs[index];
                var forward = Direction > 0;
                for (var frameLocal = 0; frameLocal < Ticks; frameLocal++)
                {
                    var frame = FrameBase + 1 + frameLocal;
                    var tick = PhaseToTick(run.Phase, frame, run.Duration, Direction);
                    for (var stepLocal = 0; stepLocal < run.StepCount; stepLocal++)
                    {
                        var flat = run.StepFlatBase + stepLocal;
                        var value = 0f;
                        if (tick >= WindowStart[flat] && tick < WindowEnd[flat])
                        {
                            if (forward)
                                value = LaneValue(ForwardValues, ForwardOffset[flat], ForwardCount[flat], tick, WindowStart[flat]);
                            else
                                value = LaneValue(BackwardValues, BackwardOffset[flat], BackwardCount[flat], tick, WindowStart[flat]);
                        }
                        Values[(index * Ticks + frameLocal) * MaxSteps + stepLocal] = value;
                    }
                }
            }

            static int PhaseToTick(ushort phase, int frame, int duration, int direction)
            {
                var t = (int)phase + direction * frame;
                t %= duration;
                if (t < 0) t += duration;
                return t;
            }

            static float LaneValue(NativeArray<float> blob, int offset, int count, int tick, int windowStart)
            {
                if (count <= 0) return 0f;
                if (count == 1) return blob[offset];
                return blob[offset + (tick - windowStart)];
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict)]
        struct RowAccumulateJob : IJobParallelFor
        {
            public NativeArray<float> Accumulator;
            [ReadOnly] public NativeArray<int> PlanStart;
            [ReadOnly] public NativeArray<PlanEntry> PlanEntries;
            [ReadOnly] public NativeArray<int> WindowStart;
            [ReadOnly] public NativeArray<int> WindowEnd;
            [ReadOnly] public NativeArray<float> Values;
            public int Ticks;
            public int FrameBase;
            public int Direction;
            public int MaxSteps;

            public void Execute(int index)
            {
                var sum = Accumulator[index];
                var start = PlanStart[index];
                var end = PlanStart[index + 1];
                for (var frameLocal = 0; frameLocal < Ticks; frameLocal++)
                {
                    var frame = FrameBase + 1 + frameLocal;
                    for (var plan = start; plan < end; plan++)
                    {
                        var entry = PlanEntries[plan];
                        var tick = PhaseToTick(entry.Phase, frame, entry.Duration, Direction);
                        if (tick < WindowStart[entry.Flat] || tick >= WindowEnd[entry.Flat]) continue;
                        sum += Values[(entry.Run * Ticks + frameLocal) * MaxSteps + entry.StepLocal];
                    }
                }
                Accumulator[index] = sum;
            }

            static int PhaseToTick(ushort phase, int frame, int duration, int direction)
            {
                var t = (int)phase + direction * frame;
                t %= duration;
                if (t < 0) t += duration;
                return t;
            }
        }

        struct Visit
        {
            public int Run;
            public int StepLocal;
            public int Flat;
            public int Row;
            public int Timeline;
            public int KindId;
            public int KindOrder;
            public int Creation;
        }

        private readonly int _rowCapacity;
        private readonly NativeArray<int> _duration;
        private readonly NativeArray<int> _stepFlatBase;
        private readonly NativeArray<int> _stepCount;
        private readonly NativeArray<int> _windowStart;
        private readonly NativeArray<int> _windowEnd;
        private readonly NativeArray<int> _stepKind;
        private readonly NativeArray<int> _stepKindOrder;
        private readonly NativeArray<int> _forwardOffset;
        private readonly NativeArray<int> _forwardCount;
        private readonly NativeArray<int> _backwardOffset;
        private readonly NativeArray<int> _backwardCount;
        private readonly NativeArray<float> _forwardValues;
        private readonly NativeArray<float> _backwardValues;
        private readonly int _maxSteps;
        private readonly bool _singleKind;
        private readonly List<int> _pbRow;
        private readonly List<ushort> _pbTimeline;
        private readonly List<ushort> _pbPhase;
        private NativeArray<RunDescriptor> _runs;
        private NativeArray<int> _planStart;
        private NativeArray<PlanEntry> _planEntries;
        private readonly NativeArray<float> _accumulator;
        private int _frame;
        private int _invalidPending;
        private bool _planDirty;
        private bool _disposed;

        public BurstWorld(global::Tl.Library library, int rowCapacity)
        {
            if (library == null) throw new ArgumentNullException("library");
            if (rowCapacity <= 0) throw new ArgumentOutOfRangeException("rowCapacity");
            _rowCapacity = rowCapacity;
            var timelineCount = library.TimelineCount;
            var durations = new int[timelineCount];
            var stepCounts = new int[timelineCount];
            var stepFlatBases = new int[timelineCount];
            var totalSteps = 0;
            for (var i = 0; i < timelineCount; i++)
            {
                var row = library.Timeline(i);
                var duration = (int)row.Duration;
                durations[i] = duration < 0 ? 0 : duration;
                var steps = (int)row.StepCount;
                stepCounts[i] = steps < 0 ? 0 : steps;
                stepFlatBases[i] = totalSteps;
                totalSteps += stepCounts[i];
            }
            var windowStarts = new int[totalSteps];
            var windowEnds = new int[totalSteps];
            var stepKinds = new int[totalSteps];
            var stepKindOrders = new int[totalSteps];
            var forwardOffsets = new int[totalSteps];
            var forwardCounts = new int[totalSteps];
            var backwardOffsets = new int[totalSteps];
            var backwardCounts = new int[totalSteps];
            var forwardBlob = new List<float>();
            var backwardBlob = new List<float>();
            var maxSteps = 0;
            for (var i = 0; i < timelineCount; i++)
            {
                if (stepCounts[i] > maxSteps) maxSteps = stepCounts[i];
                var globalBase = (int)library.Timeline(i).StepIndex;
                for (var step = 0; step < stepCounts[i]; step++)
                {
                    var flat = stepFlatBases[i] + step;
                    var globalStep = globalBase + step;
                    var row = library.Step(globalStep);
                    windowStarts[flat] = (int)row.WindowStart;
                    windowEnds[flat] = (int)row.WindowEnd;
                    stepKinds[flat] = (int)row.KindId;
                    stepKindOrders[flat] = KindOrderOf(library, (int)row.KindId);
                    forwardOffsets[flat] = forwardBlob.Count;
                    var forwardPlane = library.ForwardPlane(globalStep);
                    forwardCounts[flat] = forwardPlane.Length;
                    for (var k = 0; k < forwardPlane.Length; k++) forwardBlob.Add(forwardPlane[k]);
                    backwardOffsets[flat] = backwardBlob.Count;
                    var backwardPlane = library.BackwardPlane(globalStep);
                    backwardCounts[flat] = backwardPlane.Length;
                    for (var k = 0; k < backwardPlane.Length; k++) backwardBlob.Add(backwardPlane[k]);
                }
            }
            _duration = new NativeArray<int>(durations, Allocator.Persistent);
            _stepCount = new NativeArray<int>(stepCounts, Allocator.Persistent);
            _stepFlatBase = new NativeArray<int>(stepFlatBases, Allocator.Persistent);
            _windowStart = new NativeArray<int>(windowStarts, Allocator.Persistent);
            _windowEnd = new NativeArray<int>(windowEnds, Allocator.Persistent);
            _stepKind = new NativeArray<int>(stepKinds, Allocator.Persistent);
            _stepKindOrder = new NativeArray<int>(stepKindOrders, Allocator.Persistent);
            _forwardOffset = new NativeArray<int>(forwardOffsets, Allocator.Persistent);
            _forwardCount = new NativeArray<int>(forwardCounts, Allocator.Persistent);
            _backwardOffset = new NativeArray<int>(backwardOffsets, Allocator.Persistent);
            _backwardCount = new NativeArray<int>(backwardCounts, Allocator.Persistent);
            _forwardValues = new NativeArray<float>(forwardBlob.ToArray(), Allocator.Persistent);
            _backwardValues = new NativeArray<float>(backwardBlob.ToArray(), Allocator.Persistent);
            _maxSteps = maxSteps < 1 ? 1 : maxSteps;
            _singleKind = true;
            if (stepKinds.Length > 0)
            {
                var firstKind = stepKinds[0];
                for (var i = 1; i < stepKinds.Length; i++)
                {
                    if (stepKinds[i] != firstKind)
                    {
                        _singleKind = false;
                        break;
                    }
                }
            }
            _pbRow = new List<int>();
            _pbTimeline = new List<ushort>();
            _pbPhase = new List<ushort>();
            _runs = new NativeArray<RunDescriptor>(0, Allocator.Persistent);
            _planStart = new NativeArray<int>(rowCapacity + 1, Allocator.Persistent);
            _planEntries = new NativeArray<PlanEntry>(0, Allocator.Persistent);
            _accumulator = new NativeArray<float>(rowCapacity, Allocator.Persistent);
            _planDirty = true;
        }

        public int RowCapacity => _rowCapacity;
        public int Frame => _frame;
        public int ActiveCount => _pbRow.Count;
        public int InvalidRequests => _invalidPending;
        public int LastInvalidRequests { get; private set; }
        public NativeArray<float> Accumulator => _accumulator;

        public void Start(global::Tl.Row row, global::Tl.TimelineId timeline, int phase)
        {
            if (row.Value < 0 || row.Value >= _rowCapacity || timeline.Value == 0 || timeline.Value > _duration.Length || _duration[timeline.Value - 1] <= 0)
            {
                _invalidPending++;
                return;
            }
            _pbRow.Add(row.Value);
            _pbTimeline.Add(timeline.Value);
            _pbPhase.Add((ushort)(phase & 65535));
            _planDirty = true;
        }

        public void Tick(global::Tl.Direction direction, int workers = 0)
        {
            Run(direction, 1, workers);
        }

        public void Run(global::Tl.Direction direction, int ticks, int workers = 0)
        {
            CheckDisposed();
            if (ticks < 0) throw new ArgumentOutOfRangeException("ticks");
            if (ticks == 0) return;
            LastInvalidRequests = _invalidPending;
            _invalidPending = 0;
            if (_planDirty) RebuildPlan();
            var dir = direction == global::Tl.Direction.Reverse ? -1 : 1;
            var runCount = _runs.Length;
            var values = new NativeArray<float>(runCount * ticks * _maxSteps, Allocator.TempJob);
            try
            {
                if (runCount > 0)
                {
                    var runJob = new RunValuesJob
                    {
                        Runs = _runs,
                        ForwardOffset = _forwardOffset,
                        ForwardCount = _forwardCount,
                        BackwardOffset = _backwardOffset,
                        BackwardCount = _backwardCount,
                        ForwardValues = _forwardValues,
                        BackwardValues = _backwardValues,
                        WindowStart = _windowStart,
                        WindowEnd = _windowEnd,
                        Values = values,
                        Ticks = ticks,
                        FrameBase = _frame,
                        Direction = dir,
                        MaxSteps = _maxSteps,
                    };
                    runJob.Schedule(runCount, ParallelApplyBurst.ChunkSize(runCount, workers)).Complete();
                }
                var rowJob = new RowAccumulateJob
                {
                    Accumulator = _accumulator,
                    PlanStart = _planStart,
                    PlanEntries = _planEntries,
                    WindowStart = _windowStart,
                    WindowEnd = _windowEnd,
                    Values = values,
                    Ticks = ticks,
                    FrameBase = _frame,
                    Direction = dir,
                    MaxSteps = _maxSteps,
                };
                rowJob.Schedule(_rowCapacity, ParallelApplyBurst.ChunkSize(_rowCapacity, workers)).Complete();
            }
            finally
            {
                values.Dispose();
            }
            _frame += ticks;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _runs.Dispose();
            _planStart.Dispose();
            _planEntries.Dispose();
            _accumulator.Dispose();
            _duration.Dispose();
            _stepCount.Dispose();
            _stepFlatBase.Dispose();
            _windowStart.Dispose();
            _windowEnd.Dispose();
            _stepKind.Dispose();
            _stepKindOrder.Dispose();
            _forwardOffset.Dispose();
            _forwardCount.Dispose();
            _backwardOffset.Dispose();
            _backwardCount.Dispose();
            _forwardValues.Dispose();
            _backwardValues.Dispose();
        }

        private static int KindOrderOf(global::Tl.Library library, int kindId)
        {
            var count = library.KindCount;
            for (var k = 0; k < count; k++)
            {
                var row = library.Kind(k);
                if ((int)row.Id == kindId) return (int)row.Order;
            }
            return kindId;
        }

        private void RebuildPlan()
        {
            _planDirty = false;
            var count = _pbRow.Count;
            var sorted = new int[count];
            for (var i = 0; i < count; i++) sorted[i] = i;
            Array.Sort(sorted, delegate (int left, int right)
            {
                var byTimeline = _pbTimeline[left].CompareTo(_pbTimeline[right]);
                if (byTimeline != 0) return byTimeline;
                var byPhase = _pbPhase[left].CompareTo(_pbPhase[right]);
                if (byPhase != 0) return byPhase;
                return left.CompareTo(right);
            });
            var runs = new List<RunDescriptor>();
            var visits = new List<Visit>();
            var runStart = 0;
            while (runStart < count)
            {
                var timeline = _pbTimeline[sorted[runStart]];
                var phase = _pbPhase[sorted[runStart]];
                var runEnd = runStart + 1;
                while (runEnd < count && _pbTimeline[sorted[runEnd]] == timeline && _pbPhase[sorted[runEnd]] == phase) runEnd++;
                var runIndex = runs.Count;
                var stepCount = _stepCount[timeline - 1];
                var stepFlatBase = _stepFlatBase[timeline - 1];
                runs.Add(new RunDescriptor
                {
                    Timeline = timeline,
                    Phase = phase,
                    Duration = _duration[timeline - 1],
                    StepFlatBase = stepFlatBase,
                    StepCount = stepCount,
                });
                for (var step = 0; step < stepCount; step++)
                {
                    var flat = stepFlatBase + step;
                    for (var member = runStart; member < runEnd; member++)
                    {
                        visits.Add(new Visit
                        {
                            Run = runIndex,
                            StepLocal = step,
                            Flat = flat,
                            Row = _pbRow[sorted[member]],
                            Timeline = timeline,
                            KindId = _stepKind[flat],
                            KindOrder = _stepKindOrder[flat],
                            Creation = visits.Count,
                        });
                    }
                }
                runStart = runEnd;
            }
            List<Visit> finalOrder;
            if (visits.Count > 1 && !_singleKind)
            {
                finalOrder = new List<Visit>(visits);
                finalOrder.Sort(delegate (Visit left, Visit right)
                {
                    var byKindOrder = left.KindOrder.CompareTo(right.KindOrder);
                    if (byKindOrder != 0) return byKindOrder;
                    var byKindId = left.KindId.CompareTo(right.KindId);
                    if (byKindId != 0) return byKindId;
                    var byTimeline = left.Timeline.CompareTo(right.Timeline);
                    if (byTimeline != 0) return byTimeline;
                    var byStep = left.StepLocal.CompareTo(right.StepLocal);
                    if (byStep != 0) return byStep;
                    return left.Creation.CompareTo(right.Creation);
                });
            }
            else
            {
                finalOrder = visits;
            }
            var position = new int[_rowCapacity];
            var next = new int[_rowCapacity];
            foreach (var visit in finalOrder) position[visit.Row]++;
            var flatEntries = new PlanEntry[finalOrder.Count];
            var cursor = 0;
            for (var row = 0; row < _rowCapacity; row++)
            {
                _planStart[row] = cursor;
                cursor += position[row];
                next[row] = _planStart[row];
            }
            _planStart[_rowCapacity] = cursor;
            foreach (var visit in finalOrder)
            {
                var run = runs[visit.Run];
                var entry = new PlanEntry
                {
                    Run = visit.Run,
                    StepLocal = visit.StepLocal,
                    Phase = run.Phase,
                    Duration = run.Duration,
                    Flat = visit.Flat,
                };
                flatEntries[next[visit.Row]++] = entry;
            }
            _runs.Dispose();
            _planEntries.Dispose();
            _runs = new NativeArray<RunDescriptor>(runs.ToArray(), Allocator.Persistent);
            _planEntries = new NativeArray<PlanEntry>(flatEntries, Allocator.Persistent);
        }

        private void CheckDisposed()
        {
            if (_disposed) throw new ObjectDisposedException("BurstWorld");
        }
    }
}
