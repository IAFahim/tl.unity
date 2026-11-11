namespace Tl;

public static unsafe class Horizon
{
    public const int Max = 255;

    public static byte OfRun(Playback* slots, int count, LibraryTimeline* timelines, int timelineCount, ulong worldReverse, int limit)
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
            if (duration == 0 || duration > ushort.MaxValue || slots[i].Tick >= duration) return 0;
            var calm = SlotWord.MovesForward(word, worldReverse) ? (int)duration - 1 - slots[i].Tick : slots[i].Tick;
            if (calm <= 0) return 0;
            if (calm < horizon) horizon = calm;
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
