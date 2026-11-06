using System.Numerics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Diagnostics.CodeAnalysis;
using static System.Runtime.CompilerServices.MethodImplOptions;

namespace Tl;









internal static unsafe class LaneEncoding
{
    internal const int BucketShift = 6;
    internal const int BucketTicks = 1 << BucketShift;
    internal const int FlatMaxTicks = 9;
    internal const int SegmentedMinTicks = 2049;
    internal const int MinConstantRun = 5;
    internal const int MinRampSpan = 4;
    internal const int MaxRampSpan = 512;
    internal const int MaxDirectionSegments = ushort.MaxValue;
    internal const uint EscapeBit = 0x80000000u;

    internal static int Buckets(uint ticks) => (int)((ticks + (BucketTicks - 1)) >> BucketShift);

    [MethodImpl(AggressiveInlining)]
    internal static uint Bits(float value) => Unsafe.As<float, uint>(ref value);

    [MethodImpl(AggressiveInlining)]
    internal static int BackwardTick(int position, int duration) => position == 0 ? duration - 1 : position - 1;

    [MethodImpl(AggressiveInlining)]
    internal static float Ramp(float v0, float v1, int start, int end, int p)
        => p == start ? v0 : p == end ? v1 : v0 + (v1 - v0) * (p - start) / (end - start);

    internal static int DenseOffset(LaneSegment segment)
    {
        var bits = segment.V0;
        return Unsafe.As<float, int>(ref bits);
    }

    internal static float Value(SlotView* slot, nuint lane, bool forward, int tick)
    {
        var ticks = slot->TableTicks;
        if (forward)
        {
            var flat = slot->Forward;
            if (flat != null) return flat[lane * ticks + (nuint)tick];
        }
        else
        {
            var flat = slot->Backward;
            if (flat != null) return flat[lane * ticks + (nuint)tick];
        }
        var buckets = (nuint)Buckets(ticks);
        var dir = slot->Directory + lane * buckets * 2 + (forward ? 0u : (uint)buckets);
        var entry = dir[(nuint)tick >> BucketShift];
        var segment = slot->Segments + (entry & ~EscapeBit);
        if ((entry & EscapeBit) != 0)
            while (tick < segment->Start || tick > segment->End)
                segment++;
        return segment->Kind switch
        {
            LaneSegment.Constant => segment->V0,
            LaneSegment.Ramp => Ramp(segment->V0, segment->V1, segment->Start, segment->End, tick),
            _ => slot->Dense[(nuint)DenseOffset(*segment) + (nuint)(tick - segment->Start)]
        };
    }

    internal static int Encode(float* values, int ticks, List<LaneSegment> segments, List<float> dense)
    {
        var first = segments.Count;
        var t = 0;
        while (t < ticks)
        {
            var head = values[t];
            var run = t + 1;
            while (run < ticks && SameBits(values[run], head) && run - t <= ushort.MaxValue + 1)
                run++;
            if (run - t >= MinConstantRun)
            {
                segments.Add(new LaneSegment { Start = (ushort)t, End = (ushort)(run - 1), Kind = LaneSegment.Constant, V0 = head });
                t = run;
                continue;
            }
            if (RampHead(values, ticks, t))
            {
                var end = t + MinRampSpan - 1;
                while (end + 1 < Math.Min(ticks, t + MaxRampSpan) && RampFits(values, t, end + 1))
                    end++;
                segments.Add(new LaneSegment { Start = (ushort)t, End = (ushort)end, Kind = LaneSegment.Ramp, V0 = head, V1 = values[end] });
                t = end + 1;
                continue;
            }
            var denseStart = t;
            while (++t < ticks)
            {
                head = values[t];
                run = t + 1;
                while (run < ticks && SameBits(values[run], head) && run - t <= ushort.MaxValue + 1)
                    run++;
                if (run - t >= MinConstantRun || RampHead(values, ticks, t))
                    break;
            }
            var offset = dense.Count;
            var offsetBits = offset;
            for (var k = denseStart; k < t; k++)
                dense.Add(values[k]);
            segments.Add(new LaneSegment
            {
                Start = (ushort)denseStart,
                End = (ushort)(t - 1),
                Kind = LaneSegment.Dense,
                V0 = Unsafe.As<int, float>(ref offsetBits)
            });
        }
        return segments.Count - first;
    }

    internal static void BuildDirectory(LaneSegment* segments, int baseIndex, int count, int ticks, uint* directory)
    {
        var buckets = Buckets((uint)ticks);
        var last = baseIndex + count;
        for (var b = 0; b < buckets; b++)
        {
            var lo = b << BucketShift;
            var hi = Math.Min(lo + BucketTicks - 1, ticks - 1);
            var s = baseIndex;
            while (s < last && segments[s].End < lo)
                s++;
            var segment = segments[s];
            directory[b] = segment.Kind == LaneSegment.Constant && segment.Start <= lo && segment.End >= hi
                ? (uint)s
                : EscapeBit | (uint)s;
        }
    }

    static bool SameBits(float a, float b)
        => Unsafe.As<float, uint>(ref a) == Unsafe.As<float, uint>(ref b);

    static bool RampHead(float* values, int ticks, int t)
        => t + MinRampSpan <= ticks && RampFits(values, t, t + MinRampSpan - 1);

    static bool RampFits(float* values, int t, int end)
    {
        var v0 = values[t];
        var v1 = values[end];
        for (var i = t + 1; i < end; i++)
            if (!SameBits(values[i], Ramp(v0, v1, t, end, i)))
                return false;
        return true;
    }
}

