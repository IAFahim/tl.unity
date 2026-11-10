using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
#if NET10_0_OR_GREATER
using System.Numerics;
#endif

namespace Tl;

public readonly struct TimelineSpec
{
    public readonly uint Duration;
    public readonly bool Loop;
    public readonly StepSpec[] Steps;

    public TimelineSpec(uint duration, bool loop, StepSpec[] steps)
    {
        Duration = duration;
        Loop = loop;
        Steps = steps ?? throw new ArgumentNullException(nameof(steps));
    }
}

public readonly struct StepSpec
{
    public readonly uint WindowStart;
    public readonly uint WindowEnd;
    public readonly uint KindId;
    public readonly ulong PairKey;
    public readonly float[] ForwardLane;
    public readonly float[] BackwardLane;

    public StepSpec(uint windowStart, uint windowEnd, uint kindId, ulong pairKey, float[] forwardLane, float[] backwardLane)
    {
        WindowStart = windowStart;
        WindowEnd = windowEnd;
        KindId = kindId;
        PairKey = pairKey;
        ForwardLane = forwardLane ?? throw new ArgumentNullException(nameof(forwardLane));
        BackwardLane = backwardLane ?? throw new ArgumentNullException(nameof(backwardLane));
    }
}

public static class LibraryWriter
{
    public static byte[] Write(IReadOnlyList<TimelineSpec> timelines)
    {
        if (timelines == null) throw new ArgumentNullException(nameof(timelines));
        var timelineCount = timelines.Count;
        var stepBases = new uint[timelineCount];
        var stepCounts = new uint[timelineCount];
        var totalSteps = 0ul;
        for (var t = 0; t < timelineCount; t++)
        {
            var spec = timelines[t];
            if (spec.Duration == 0) throw new ArgumentException("Timeline duration must be positive.", nameof(timelines));
            if (spec.Steps == null) throw new ArgumentException($"Timeline {t} steps are null.", nameof(timelines));
            stepBases[t] = (uint)totalSteps;
            stepCounts[t] = (uint)spec.Steps.Length;
            totalSteps += (uint)spec.Steps.Length;
        }
        var stepTotal = (int)totalSteps;
        var forwardClasses = new LaneClass[stepTotal];
        var backwardClasses = new LaneClass[stepTotal];
        var forwardOffsets = new uint[stepTotal];
        var backwardOffsets = new uint[stepTotal];
        var forwardCounts = new uint[stepTotal];
        var backwardCounts = new uint[stepTotal];
        var kindIds = new List<uint>();
        var valueCursor = 0ul;
        var stepIndex = 0;
        for (var t = 0; t < timelineCount; t++)
        {
            var spec = timelines[t];
            for (var s = 0; s < spec.Steps.Length; s++, stepIndex++)
            {
                var step = spec.Steps[s];
                if (step.ForwardLane == null || step.BackwardLane == null)
                    throw new ArgumentException($"Step {stepIndex} lanes are null.", nameof(timelines));
                if (step.WindowStart >= step.WindowEnd || step.WindowEnd > spec.Duration)
                    throw new ArgumentException($"Step {stepIndex} window is outside its timeline duration.", nameof(timelines));
                var window = step.WindowEnd - step.WindowStart;
                CheckLane(step.ForwardLane, window, stepIndex, "Forward");
                CheckLane(step.BackwardLane, window, stepIndex, "Backward");
                forwardClasses[stepIndex] = IsConstantLane(step.ForwardLane) ? LaneClass.Constant : LaneClass.Dense;
                backwardClasses[stepIndex] = IsConstantLane(step.BackwardLane) ? LaneClass.Constant : LaneClass.Dense;
                forwardOffsets[stepIndex] = (uint)valueCursor;
                forwardCounts[stepIndex] = forwardClasses[stepIndex] == LaneClass.Constant ? 1u : window;
                valueCursor += forwardCounts[stepIndex];
                backwardOffsets[stepIndex] = (uint)valueCursor;
                backwardCounts[stepIndex] = backwardClasses[stepIndex] == LaneClass.Constant ? 1u : window;
                valueCursor += backwardCounts[stepIndex];
                kindIds.Add(step.KindId);
            }
        }
        kindIds.Sort();
        var distinctKinds = new List<uint>();
        foreach (var id in kindIds)
            if (distinctKinds.Count == 0 || distinctKinds[distinctKinds.Count - 1] != id)
                distinctKinds.Add(id);
        var kindCount = distinctKinds.Count;
        var names = new List<string>(timelineCount + kindCount);
        for (var t = 0; t < timelineCount; t++)
            names.Add("Timeline" + t);
        for (var k = 0; k < kindCount; k++)
            names.Add("Kind" + distinctKinds[k]);
        var nameBytes = new byte[names.Count][];
        var nameOffsets = new uint[names.Count];
        var nameLengths = new uint[names.Count];
        var nameByteTotal = 0ul;
        for (var n = 0; n < names.Count; n++)
        {
            nameBytes[n] = Encoding.UTF8.GetBytes(names[n]);
            nameOffsets[n] = (uint)nameByteTotal;
            nameLengths[n] = (uint)nameBytes[n].Length;
            nameByteTotal += (ulong)nameBytes[n].Length;
        }
        ulong edgeCount = 0;
        for (var t = 0; t < timelineCount; t++)
            if (stepCounts[t] > 0) edgeCount += stepCounts[t] - 1ul;
        var counts = new uint[10]
        {
            (uint)timelineCount,
            (uint)timelineCount,
            (uint)totalSteps,
            (uint)timelineCount,
            (uint)edgeCount,
            (uint)valueCursor,
            (uint)totalSteps,
            (uint)kindCount,
            (uint)totalSteps,
            (uint)names.Count,
        };
        var sizes = new ulong[10]
        {
            Tlib3Layout.Size<Tlib3Timeline>(),
            Tlib3Layout.Size<Tlib3Stage>(),
            Tlib3Layout.Size<Tlib3Step>(),
            Tlib3Layout.Size<Tlib3StagePlan>(),
            Tlib3Layout.Size<Tlib3Edge>(),
            Tlib3Layout.Size<float>(),
            Tlib3Layout.Size<Tlib3Marker>(),
            Tlib3Layout.Size<Tlib3Kind>(),
            Tlib3Layout.Size<Tlib3Feeder>(),
            Tlib3Layout.Size<Tlib3Name>(),
        };
        var offsets = new uint[10];
        var pos = (ulong)Tlib3Layout.HeaderBytes;
        for (var i = 0; i < 10; i++)
        {
            pos = Tlib3Layout.Align(pos);
            offsets[i] = (uint)pos;
            pos += (ulong)counts[i] * sizes[i];
        }
        pos += nameByteTotal;
        pos = Tlib3Layout.Align(pos);
        var shaOffset = pos;
        var total = shaOffset + Tlib3Layout.TrailerBytes;
        if (total > int.MaxValue)
            throw new ArgumentException("Library exceeds the single-blob size limit.", nameof(timelines));
        var blob = new byte[(int)total];
        var header = new Tlib3Header
        {
            Magic = Tlib3Layout.Magic,
            Version = Tlib3Layout.Version,
            TotalBytes = (uint)total,
            Flags = 1,
            TimelineOffset = offsets[0], TimelineCount = counts[0],
            StageOffset = offsets[1], StageCount = counts[1],
            StepOffset = offsets[2], StepCount = counts[2],
            StagePlanOffset = offsets[3], StagePlanCount = counts[3],
            EdgeOffset = offsets[4], EdgeCount = counts[4],
            ValueOffset = offsets[5], ValueCount = counts[5],
            MarkerOffset = offsets[6], MarkerCount = counts[6],
            KindOffset = offsets[7], KindCount = counts[7],
            FeederOffset = offsets[8], FeederCount = counts[8],
            NameOffset = offsets[9], NameCount = counts[9],
            NameBytes = (uint)nameByteTotal,
            ShaOffset = (uint)shaOffset,
        };
#if NET10_0_OR_GREATER
        MemoryMarshal.Write(blob.AsSpan(), in header);
#else
        MemoryMarshal.Write(blob.AsSpan(), ref header);
#endif
        var span = blob.AsSpan();
        var valueBase = (int)offsets[5];
        var timelineRow = (int)offsets[0];
        var stageRow = (int)offsets[1];
        var stepRow = (int)offsets[2];
        var planRow = (int)offsets[3];
        var edgeRow = (int)offsets[4];
        var markerRow = (int)offsets[6];
        var feederRow = (int)offsets[8];
        stepIndex = 0;
        for (var t = 0; t < timelineCount; t++)
        {
            var spec = timelines[t];
            WriteRow(span.Slice(timelineRow), new Tlib3Timeline
            {
                Duration = spec.Duration,
                StageIndex = (uint)t,
                StageCount = 1,
                StepIndex = stepBases[t],
                StepCount = stepCounts[t],
                Loop = spec.Loop ? (byte)1 : (byte)0,
                Flags = 0,
                NameIndex = (ushort)t,
            });
            timelineRow += 24;
            WriteRow(span.Slice(stageRow), new Tlib3Stage
            {
                Start = 0,
                End = spec.Duration,
                StepIndex = stepBases[t],
                StepCount = stepCounts[t],
            });
            stageRow += 16;
            WriteRow(span.Slice(planRow), new Tlib3StagePlan
            {
                StageIndex = (uint)t,
                StepIndex = stepBases[t],
                StepCount = stepCounts[t],
                Reserved = 0,
            });
            planRow += 16;
            for (var s = 0; s < spec.Steps.Length; s++, stepIndex++)
            {
                var step = spec.Steps[s];
                WriteRow(span.Slice(stepRow), new Tlib3Step
                {
                    WindowStart = step.WindowStart,
                    WindowEnd = step.WindowEnd,
                    KindId = step.KindId,
                    PairKey = step.PairKey,
                    ForwardOffset = forwardOffsets[stepIndex],
                    ForwardCount = forwardCounts[stepIndex],
                    BackwardOffset = backwardOffsets[stepIndex],
                    BackwardCount = backwardCounts[stepIndex],
                    ForwardClass = (byte)forwardClasses[stepIndex],
                    BackwardClass = (byte)backwardClasses[stepIndex],
                    Reserved = 0,
                    Reserved2 = 0,
                });
                stepRow += 48;
                WriteLane(blob, valueBase + (int)(forwardOffsets[stepIndex] * 4u), step.ForwardLane, forwardClasses[stepIndex]);
                WriteLane(blob, valueBase + (int)(backwardOffsets[stepIndex] * 4u), step.BackwardLane, backwardClasses[stepIndex]);
                WriteRow(span.Slice(markerRow), new Tlib3Marker
                {
                    Tick = step.WindowStart,
                    StepIndex = (uint)stepIndex,
                    TimelineIndex = (uint)t,
                    Kind = 0,
                    Flags = 0,
                    Reserved = 0,
                });
                markerRow += 16;
                WriteRow(span.Slice(feederRow), new Tlib3Feeder
                {
                    KindId = step.KindId,
                    TimelineIndex = (uint)t,
                    StepIndex = (uint)stepIndex,
                    Flags = 0,
                });
                feederRow += 16;
                if (s + 1 < spec.Steps.Length)
                {
                    WriteRow(span.Slice(edgeRow), new Tlib3Edge
                    {
                        From = (uint)stepIndex,
                        To = (uint)(stepIndex + 1),
                        Kind = 0,
                        Reserved = 0,
                    });
                    edgeRow += 16;
                }
            }
        }
        var kindRow = (int)offsets[7];
        for (var k = 0; k < kindCount; k++, kindRow += 16)
        {
            WriteRow(span.Slice(kindRow), new Tlib3Kind
            {
                Id = distinctKinds[k],
                Order = (uint)k,
                Flags = 0,
                NameIndex = (uint)(timelineCount + k),
            });
        }
        var nameRowAt = (int)offsets[9];
        for (var n = 0; n < names.Count; n++, nameRowAt += 8)
        {
            WriteRow(span.Slice(nameRowAt), new Tlib3Name { Offset = nameOffsets[n], Length = nameLengths[n] });
            nameBytes[n].CopyTo(span.Slice((int)offsets[9] + names.Count * 8 + (int)nameOffsets[n]));
        }
        using (var sha = SHA256.Create())
        {
            var digest = sha.ComputeHash(blob, 0, (int)shaOffset);
            digest.CopyTo(blob, (int)shaOffset);
        }
        return blob;
    }

    static void WriteRow<T>(Span<byte> destination, T value) where T : unmanaged
    {
#if NET10_0_OR_GREATER
        MemoryMarshal.Write(destination, in value);
#else
        MemoryMarshal.Write(destination, ref value);
#endif
    }

    static void WriteLane(byte[] blob, int byteOffset, float[] lane, LaneClass laneClass)
    {
        if (laneClass == LaneClass.Constant)
        {
#if NET10_0_OR_GREATER
            MemoryMarshal.Write(blob.AsSpan().Slice(byteOffset), in lane[0]);
#else
            MemoryMarshal.Write(blob.AsSpan().Slice(byteOffset), ref lane[0]);
#endif
            return;
        }
        Buffer.BlockCopy(lane, 0, blob, byteOffset, lane.Length * 4);
    }

    static void CheckLane(float[] lane, uint window, int stepIndex, string direction)
    {
        if (lane == null)
            throw new ArgumentException($"Step {stepIndex} {direction} lane is null.");
        if (lane.Length != 0 && lane.Length != 1 && lane.Length != (int)window)
            throw new ArgumentException($"Step {stepIndex} {direction} lane length {lane.Length} matches neither its window {window} nor a single constant value.");
        if (lane.Length == 0)
            throw new ArgumentException($"Step {stepIndex} {direction} lane is empty.");
    }

    static unsafe bool IsConstantLane(float[] lane)
    {
        var bits = Bits(lane[0]);
        var length = lane.Length;
        var i = 0;
#if NET10_0_OR_GREATER
        if (length >= Vector<float>.Count)
        {
            var mask = new Vector<float>(lane[0]);
            for (; i + Vector<float>.Count <= length; i += Vector<float>.Count)
            {
                var chunk = Vector.LoadUnsafe(ref lane[i]);
                var unequal = ~Vector.Equals(chunk, mask);
                if (Vector.Sum(unequal) != 0) return false;
            }
        }
#endif
        for (; i < length; i++)
            if (Bits(lane[i]) != bits) return false;
        return true;
    }

    static unsafe uint Bits(float value)
    {
        return *(uint*)&value;
    }
}
