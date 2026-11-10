using System;
#if NET10_0_OR_GREATER
using System.Numerics;
#endif

namespace Tl;

public readonly struct Run
{
    public readonly int Start;
    public readonly int Length;
    public readonly ushort Phase;

    public Run(int start, int length, ushort phase)
    {
        Start = start;
        Length = length;
        Phase = phase;
    }
}

public static class Kernels
{
    public static int PhaseToTick(ushort phase, int frame, int duration, int dir)
    {
        var t = (int)phase + dir * frame;
        t %= duration;
        if (t < 0) t += duration;
        return t;
    }

    public static void BroadcastDense(float[] acc, int off, float value, int count)
    {
        var i = 0;
#if NET10_0_OR_GREATER
        var v = new Vector<float>(value);
        for (; i + Vector<float>.Count <= count; i += Vector<float>.Count)
            Vector.StoreUnsafe(Vector.LoadUnsafe(ref acc[off + i]) + v, ref acc[off + i]);
#endif
        for (; i < count; i++) acc[off + i] += value;
    }

    public static void BroadcastSparse(float[] acc, int off, int[] row, int start, int length, float value)
    {
        for (var i = start; i < start + length; i++) acc[off + row[i]] += value;
    }

    public static void LaneDense(float[] acc, int off, float[] lane, int count)
    {
        var i = 0;
#if NET10_0_OR_GREATER
        for (; i + Vector<float>.Count <= count; i += Vector<float>.Count)
            Vector.StoreUnsafe(Vector.LoadUnsafe(ref acc[off + i]) + Vector.LoadUnsafe(ref lane[i]), ref acc[off + i]);
#endif
        for (; i < count; i++) acc[off + i] += lane[i];
    }

    public static void LaneGatherScalar(float[] acc, int off, int[] row, ushort[] phase, float[] lane, int frame, int duration, int dir)
    {
        for (var i = 0; i < row.Length; i++)
            acc[off + row[i]] += lane[PhaseToTick(phase[i], frame, duration, dir)];
    }

    public static void RunBroadcast(float[] acc, int off, int[] sortedRow, Run[] runs, float[] lane, int frame, int duration, int dir)
    {
        for (var r = 0; r < runs.Length; r++)
        {
            var run = runs[r];
            var value = lane[PhaseToTick(run.Phase, frame, duration, dir)];
            BroadcastSparse(acc, off, sortedRow, run.Start, run.Length, value);
        }
    }

    public static void StableRadix(uint[] keys, int[] order, int[] scratch)
    {
        var n = order.Length;
        if (n <= 1) return;
        Span<int> cnt = stackalloc int[256];
        var src = order;
        var dst = scratch;
        for (var pass = 0; pass < 4; pass++)
        {
            cnt.Clear();
            for (var i = 0; i < n; i++) cnt[(int)((keys[src[i]] >> (pass * 8)) & 255u)]++;
            var sum = 0;
            for (var b = 0; b < 256; b++)
            {
                var c = cnt[b];
                cnt[b] = sum;
                sum += c;
            }
            for (var i = 0; i < n; i++)
            {
                var id = src[i];
                var slot = (int)((keys[id] >> (pass * 8)) & 255u);
                dst[cnt[slot]++] = id;
            }
            var tmp = src;
            src = dst;
            dst = tmp;
        }
        if (src != order)
        {
            for (var i = 0; i < n; i++) order[i] = src[i];
        }
    }
}
