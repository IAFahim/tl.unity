using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

public unsafe interface IKindSteps
{
    LibraryStep* StepOf(ushort timeline, int window, ushort tick);
}

[StructLayout(LayoutKind.Sequential, Size = 16)]
public unsafe struct KindSpan
{
    public LibraryStep* Step;
    public ushort Window;
    public ushort Feeder;
    public byte Ordinal;
}

public unsafe struct KindScore
{
    public int* CellBase;
    public int* Start;
    public KindSpan* Spans;
    public LibraryKindTimeline* Masks;
    public int Cells;
    public int TimelineCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CellOf(ushort timeline, int tick, int kind)
    {
        if ((uint)(timeline - 1) >= (uint)TimelineCount) return -1;
        var mask = Masks[timeline - 1].KindMask;
        var bit = 1ul << kind;
        return (mask & bit) == 0 ? -1 : CellBase[timeline - 1] + tick * KindStore.Pop(mask) + KindStore.Pop(mask & (bit - 1));
    }

    public static bool Listed(PlaybackFlags f) => (f & (PlaybackFlags.StartPending | PlaybackFlags.Ended | PlaybackFlags.Stopping)) == 0 && (f & (PlaybackFlags.ReenterPending | PlaybackFlags.Began)) != PlaybackFlags.ReenterPending;

    public static bool Synthetic(PlaybackFlags f, int tick, LibraryStep* step, bool backward)
        => (f & PlaybackFlags.Began) != 0 && ((f & PlaybackFlags.ReenterPending) != 0 || tick != (backward ? step->WindowEnd - 1 : step->WindowStart));

    public static KindScore Build<TSteps>(in KindTables t, ref TSteps steps) where TSteps : struct, IKindSteps
    {
        var s = new KindScore { CellBase = (int*)KindStore.Zeroed((nuint)(t.TimelineCount + 1) * 4), Masks = t.Sec.Timelines, TimelineCount = t.KindCount == 0 ? 0 : t.TimelineCount };
        for (var i = 0; i < s.TimelineCount; i++)
        {
            s.CellBase[i] = s.Cells;
            s.Cells += (int)t.Timelines[i].Duration * KindStore.Pop(t.Sec.Timelines[i].KindMask);
        }
        s.CellBase[s.TimelineCount] = s.Cells;
        var cells = new List<int>();
        var spans = new List<KindSpan>();
        for (var i = 0; i < s.TimelineCount; i++)
        {
            var tk = t.Sec.Timelines + i;
            if (tk->KindMask == 0) continue;
            var id = (ushort)(i + 1);
            var windows = t.Sec.Windows(tk);
            for (var k = t.PairStart[i]; k < t.PairStart[i + 1]; k++)
            {
                var w = (int)(t.Pairs[k] >> 16);
                var f = (ushort)(t.Pairs[k] & 0xffff);
                if (t.Sec.Feeders[f].Trigger != 0) continue;
                for (int tick = windows[w].Start; tick < windows[w].End; tick++)
                {
                    var cell = s.CellOf(id, tick, t.Sec.Feeders[f].Kind);
                    if (cell < 0)
                    {
                        s.Free();
                        throw new ArgumentException($"timeline {id} feeds kind {t.Sec.Feeders[f].Kind} outside its kind mask; build the score from a validated kind section.");
                    }
                    cells.Add(cell);
                    spans.Add(new KindSpan { Step = steps.StepOf(id, w, (ushort)tick), Window = (ushort)w, Feeder = f, Ordinal = t.Sec.Feeders[f].Ordinal });
                }
            }
        }
        s.Start = (int*)KindStore.Zeroed((nuint)(s.Cells + 1) * 4);
        s.Spans = (KindSpan*)KindStore.Zeroed((nuint)Math.Max(1, spans.Count) * 16);
        foreach (var cell in cells) s.Start[cell]++;
        for (var n = 1; n <= s.Cells; n++) s.Start[n] += s.Start[n - 1];
        for (var n = cells.Count; n-- > 0;) s.Spans[--s.Start[cells[n]]] = spans[n];
        return s;
    }

    public void Free()
    {
        NativeMemory.AlignedFree(CellBase);
        NativeMemory.AlignedFree(Start);
        NativeMemory.AlignedFree(Spans);
        this = default;
    }
}
