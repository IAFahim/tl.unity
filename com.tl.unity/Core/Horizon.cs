namespace Tl;

public static unsafe class Horizon
{
    public const int Max = 255;

    public static byte OfRun(Playback* slots, int count, LibraryTimeline* timelines, int timelineCount, ulong worldReverse, int limit)
        => Of(slots, count, timelines, timelineCount, default, worldReverse, limit);

    public static byte OfKindRun(Playback* slots, int count, in KindTables tables, in KindCalm calm, ulong worldReverse, int limit)
        => calm.Start == null ? (byte)0 : Of(slots, count, tables.Timelines, tables.TimelineCount, calm, worldReverse, limit);

    static byte Of(Playback* slots, int count, LibraryTimeline* timelines, int timelineCount, KindCalm calm, ulong worldReverse, int limit)
    {
        if (limit <= 0 || count <= 0 || timelineCount <= 0 || slots == null || timelines == null) return 0;
        var horizon = limit < Max ? limit : Max;
        var plain = false;
        for (var i = 0; i < count; i++)
        {
            var word = ((ulong*)slots)[i];
            if ((word & SlotWord.TimelineMask) == 0 || SlotWord.IsInert(word)) continue;
            var index = (uint)(slots[i].Timeline - 1);
            if (SlotWord.PlainMask(word) == 0 || index >= (uint)timelineCount) return 0;
            var duration = timelines[index].Duration;
            int tick = slots[i].Tick;
            if (duration == 0 || duration > ushort.MaxValue || tick >= duration) return 0;
            var forward = SlotWord.MovesForward(word, worldReverse);
            var at = calm.Start == null ? -1 : calm.Start[index];
            var steps = at >= 0 ? (forward ? calm.Forward : calm.Backward)[at + tick] : forward ? (int)duration - 1 - tick : tick;
            if (steps <= 0) return 0;
            if (steps < horizon) horizon = steps;
            plain = true;
        }
        return plain ? (byte)horizon : (byte)0;
    }

    internal static bool Decrement64(byte* horizons)
    {
        const ulong ones = 0x0101010101010101ul;
        const ulong highs = 0x8080808080808080ul;
        var words = (ulong*)horizons;
        for (var i = 0; i < 8; i++)
        {
            var word = words[i];
            if ((unchecked(word - ones) & ~word & highs) != 0) return false;
        }
        for (var i = 0; i < 8; i++) words[i] = unchecked(words[i] - ones);
        return true;
    }
}
