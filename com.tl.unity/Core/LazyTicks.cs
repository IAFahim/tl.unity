using System;
using System.Numerics;
#if NET
using System.Runtime.Intrinsics;
#endif

namespace Tl;

internal static unsafe class LazyTicks
{
    public const int Far = 0xFFFF;

    static bool Up(ulong word, bool forward) => SlotWord.MovesForward(word, SlotWord.WorldReverse(forward));

    public static void Shift(Playback* slots, int count, int by, bool forward)
    {
        for (var i = 0; i < count; i++)
        {
            var word = ((ulong*)slots)[i];
            if (SlotWord.PlainMask(word) != 0) slots[i].Tick += (ushort)(Up(word, forward) ? by : -by);
        }
    }

    public static ushort Settle(Playback* slot, LibraryTimeline* timelines, int timelineCount, int clock, bool forward)
    {
        var word = *(ulong*)slot;
        if (SlotWord.PlainMask(word) == 0) return (ushort)(clock + ((word & SlotWord.TimelineMask) == 0 || SlotWord.IsInert(word) ? Far : 1));
        var index = (uint)(slot->Timeline - 1);
        var up = Up(word, forward);
        uint at = slot->Tick;
        var calm = 0u;
        if (index < (uint)timelineCount)
        {
            var duration = timelines[index].Duration;
            calm = up ? at + 1 < duration ? duration - 1 - at : 0 : duration != 0 ? at : 0;
        }
        slot->Tick = (ushort)(up ? at - clock : at + clock);
        return (ushort)(clock + 1 + calm);
    }

    public static int Scan(ushort* due, int n, int tick, ulong* hits, ref int held, ulong key)
    {
        var near = Far;
        var i = 0;
#if NET
        if (Vector128.IsHardwareAccelerated && n >= 8)
        {
            var best = Vector128.Create((ushort)Far);
            for (; i <= n - 8; i += 8)
            {
                var d = Vector128.Load(due + i) - Vector128.Create((ushort)(tick + 1));
                best = Vector128.Min(best, d);
                for (var bits = Vector128.Equals(d, Vector128<ushort>.AllBitsSet).AsByte().ExtractMostSignificantBits() & 0x5555; bits != 0; bits &= bits - 1)
                    hits[held++] = key + (ulong)(i + (BitOperations.TrailingZeroCount(bits) >> 1));
            }
            best = Vector128.Min(best, (best.AsUInt64() >> 16).AsUInt16());
            best = Vector128.Min(best, (best.AsUInt64() >> 32).AsUInt16());
            near = Math.Min(best[0], best[4]);
        }
#else
        var w = Vector<ushort>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var best = new Vector<ushort>(Far);
            var far = best;
            for (; i <= n - w; i += w)
            {
                var d = *(Vector<ushort>*)(due + i) - new Vector<ushort>((ushort)(tick + 1));
                best = Vector.Min(best, d);
                if (Vector.EqualsAny(d, far))
                    for (var j = 0; j < w; j++)
                        if (d[j] == Far) hits[held++] = key + (ulong)(i + j);
            }
            for (var j = 0; j < w; j++) near = Math.Min(near, best[j]);
        }
#endif
        for (; i < n; i++)
        {
            var d = (ushort)(due[i] - tick - 1);
            if (d == Far) hits[held++] = key + (ulong)i;
            near = Math.Min(near, d);
        }
        return near;
    }
}
