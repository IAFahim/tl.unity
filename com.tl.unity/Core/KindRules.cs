using System.Runtime.CompilerServices;

namespace Tl;

public unsafe struct KindTables
{
    public LibraryTimeline* Timelines;
    public int TimelineCount;
    public KindSectionView Sec;
    public int KindCount;
    public ulong* Closure;
    public uint* Pairs;
    public int* PairStart;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Reaches(ushort timeline, ulong reach) => KindCount == 0 || (Closure[timeline - 1] & ~reach) == 0;
}

public unsafe interface IKindRows
{
    KindEntry* Append(int row, LibraryFeeder* feeder);
    void RemoveSpan(int row, int kind, byte slot, ushort timeline, ushort step);
}

public static unsafe class KindRules
{
    public static void Close<TRows>(in KindTables t, ref TRows rows, int row, ref Playback s, int slot) where TRows : struct, IKindRows
    {
        Stop(in t, ref rows, row, in s, slot);
        s.Flags = (s.Flags & (PlaybackFlags.Reverse | PlaybackFlags.Paused | PlaybackFlags.Ended | PlaybackFlags.StartPending)) | PlaybackFlags.Stopping;
    }

    static void Stop<TRows>(in KindTables t, ref TRows rows, int row, in Playback s, int slot) where TRows : struct, IKindRows
    {
        if (t.KindCount == 0 || (s.Flags & (PlaybackFlags.StartPending | PlaybackFlags.Ended)) != 0) return;
        var id = s.Timeline;
        var tk = t.Sec.Timelines + (id - 1);
        if (tk->KindMask == 0) return;
        var windows = t.Sec.Windows(tk);
        int tick = s.Tick;
        for (var k = t.PairStart[id - 1]; k < t.PairStart[id]; k++)
        {
            var w = (int)(t.Pairs[k] >> 16);
            if (tick < windows[w].Start || tick >= windows[w].End) continue;
            var feeder = t.Sec.Feeders + (t.Pairs[k] & 0xffff);
            if (feeder->Trigger == 0) rows.RemoveSpan(row, feeder->Kind, (byte)slot, id, (ushort)w);
            else if (feeder->Trigger == 2 && tick < windows[w].End - 1) Append(ref rows, row, feeder, slot, id, w, KindEntryFlags.Pulse | KindEntryFlags.Interrupted);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Append<TRows>(ref TRows rows, int row, LibraryFeeder* feeder, int slot, ushort timeline, int window, KindEntryFlags flags) where TRows : struct, IKindRows
    {
        var entry = rows.Append(row, feeder);
        if (entry == null) return;
        entry->Timeline = timeline;
        entry->Step = (ushort)window;
        entry->Slot = (byte)slot;
        entry->Flags = flags;
    }
}
