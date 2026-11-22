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

public unsafe interface IKindSpanValues<T> where T : unmanaged
{
    bool Evaluate(int feeder, LibraryStep* step, LibraryTimeline* timeline, ushort tick, bool backward, out T value);
    void Combine(ref T total, in T next);
}

public unsafe interface IKindFill
{
    void Add(ref KindBuild build);
}

public unsafe struct KindBuild
{
    internal KindScore Score;
    internal LibraryTimeline* Timelines;
    internal KindValues* Views;
    public ulong Kinds;
    public int Stride;
    internal int Align;
    internal int Shift;
    public int Rows;
    internal bool ByFeeder;
    internal bool Filling;
    internal bool Split;

    public static KindBuild Of<TFill>(in KindScore score, LibraryTimeline* timelines, KindValues* views, int* row, bool byFeeder, ref TFill fill) where TFill : struct, IKindFill
        => Of(in score, timelines, views, row, byFeeder, ref fill, false);

    internal static KindBuild Of<TFill>(in KindScore score, LibraryTimeline* timelines, KindValues* views, int* row, bool byFeeder, ref TFill fill, bool dense) where TFill : struct, IKindFill
    {
        var half = dense ? RowSpace(timelines, score.TimelineCount, row) : -1;
        var b = new KindBuild { Score = score, Timelines = timelines, Views = views, ByFeeder = byFeeder, Stride = 4, Align = 4, Split = half >= 0 };
        fill.Add(ref b);
        b.Stride = (b.Stride + b.Align - 1) / b.Align * b.Align;
        if (half >= 0) b.Rows = 1 + 2 * half;
        else
            for (var t = 0; t < score.TimelineCount; t++)
            {
                row[t] = (score.Masks[t].KindMask & b.Kinds) == 0 ? -1 : b.Rows;
                if (row[t] >= 0) b.Rows += (int)timelines[t].Duration;
            }
        var owner = 0;
        var rest = (long)Math.Max(1, score.TimelineCount) * 4 + (long)b.Rows * b.Stride * (b.Split ? 2 : 1);
        for (var k = 0; k < 64; k++)
            if ((b.Kinds >> k & 1) != 0)
            {
                views[k].Row = row;
                views[k].Stride = b.Stride;
                views[k].TimelineCount = score.TimelineCount;
                views[k].Rows = b.Rows;
                views[k].Reversed = half > 0 ? half : 0;
                if (views[k].Shift == 0) owner = k;
                else rest -= views[k].Bytes = (long)b.Rows * 2 * (views[k].Back - views[k].Front);
                if (b.Split) views[k].Back = views[k].Front + b.Rows * b.Stride;
            }
        if (b.Kinds != 0) views[owner].Bytes = rest;
        return b;
    }

    public static int RowSpace(LibraryTimeline* timelines, int count, int* row)
    {
        var at = 1L;
        for (var t = 0; t < count; t++)
        {
            row[t] = (int)at;
            at += timelines[t].Duration;
        }
        return 2 * (at - 1) + 1 <= 65536 ? (int)(at - 1) : -1;
    }

    public void Add<T, TSpans>(int kind, int part, ushort* routes, ref TSpans spans, T identity = default) where T : unmanaged where TSpans : struct, IKindSpanValues<T>
    {
        if ((uint)kind >= 64 || routes == null) return;
        var v = Views + kind;
        if (Filling)
        {
            if ((Kinds >> kind & 1) != 0) v->Fill(in Score, Timelines, kind, routes, ByFeeder, ref spans, identity);
            return;
        }
        if ((Kinds >> kind & 1) != 0 || Shift > 27) return;
        var align = Math.Min(64, sizeof(AlignmentProbe<T>) - sizeof(T));
        var at = (Stride + align - 1) / align * align;
        *v = new KindValues { Front = at, Back = at + sizeof(T), Shift = Shift, Part = part };
        Stride = at + (Split ? 1 : 2) * sizeof(T);
        Align = Math.Max(Align, align);
        Shift += 3;
        Kinds |= 1ul << kind;
    }

    public void Fill<TFill>(byte* data, ref TFill fill) where TFill : struct, IKindFill
    {
        for (var k = 0; k < 64; k++)
            if ((Kinds >> k & 1) != 0) Views[k].Data = data;
        Filling = true;
        fill.Add(ref this);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AlignmentProbe<T> where T : unmanaged
    {
        byte _lead;
        T _value;
    }
}

public unsafe struct KindValues
{
    public int* Row;
    public byte* Data;
    public int Stride;
    public int Front;
    public int Back;
    public int Shift;
    public int TimelineCount;
    public int Part;
    public int Rows;
    public int Reversed;
    public long Bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint* HeadOf(ushort timeline, ushort tick)
    {
        var t = (uint)(timeline - 1);
        if (t >= (uint)TimelineCount || Row[t] < 0) return null;
        return (uint*)(Data + (nuint)(Row[t] + tick) * (nuint)Stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* RowOf(ushort timeline, ushort tick)
    {
        var head = HeadOf(timeline, tick);
        return head == null || (*head >> Shift & 1) == 0 ? null : (byte*)head;
    }

    public void Fill<T, TSpans>(in KindScore score, LibraryTimeline* timelines, int kind, ushort* routes, bool byFeeder, ref TSpans spans, T identity = default) where T : unmanaged where TSpans : struct, IKindSpanValues<T>
    {
        for (var t = 0; t < TimelineCount; t++)
            for (var tick = 0; Row[t] >= 0 && tick < timelines[t].Duration; tick++)
            {
                var cell = score.CellOf((ushort)(t + 1), tick, kind);
                if (cell < 0) break;
                var at = Data + (nuint)(Row[t] + tick) * (nuint)Stride;
                var mirror = Reversed == 0 ? null : Data + (nuint)(Row[t] + Reversed + (int)timelines[t].Duration - 1 - tick) * (nuint)Stride;
                if (score.Start[cell] != score.Start[cell + 1])
                {
                    *(uint*)at |= 1u << Shift;
                    if (mirror != null) *(uint*)mirror |= 1u << Shift;
                }
                for (var d = 0; d < 2; d++)
                {
                    var has = false;
                    T total = default;
                    for (var f = score.Start[cell]; f < score.Start[cell + 1]; f++)
                    {
                        var span = score.Spans + f;
                        var route = routes[byFeeder ? span->Feeder : span->Ordinal];
                        if ((route >> 8) != Part || span->Step == null || !spans.Evaluate(route & 0xff, span->Step, timelines + t, (ushort)tick, d == 1, out var next)) continue;
                        if (has) spans.Combine(ref total, in next);
                        else
                        {
                            total = next;
                            has = true;
                        }
                    }
                    if (!has) continue;
                    *(uint*)at |= 2u << (Shift + d);
                    *(T*)(at + (d == 0 ? Front : Back)) = total;
                    if (mirror == null) continue;
                    *(uint*)mirror |= 2u << (Shift + 1 - d);
                    *(T*)(mirror + (d == 0 ? Back : Front)) = total;
                }
            }
        for (var r = 0; r < Rows; r++)
            for (var d = 0; d < 2; d++)
            {
                var at = Data + (nuint)r * (nuint)Stride;
                if ((*(uint*)at >> (Shift + 1 + d) & 1) == 0) *(T*)(at + (d == 0 ? Front : Back)) = identity;
            }
    }

    public void Free()
    {
        if (Shift == 0)
        {
            NativeMemory.AlignedFree(Row);
            NativeMemory.AlignedFree(Data);
        }
        this = default;
    }
}

public unsafe struct KindPulseValues
{
    public int* Start;
    public byte* Data;
    public int Stride;
    public int Cells;
    internal int Part;
    public long Bytes;

    internal static KindPulseValues Build<T, TEval, TSteps>(in KindTables t, int kind, int part, ushort* routes, ref TEval eval, ref TSteps steps)
        where T : unmanaged where TEval : struct, IKindSpanValues<T> where TSteps : struct, IKindSteps
    {
        var cells = 0;
        for (var tl = 0; tl < t.TimelineCount; tl++)
            if (t.PulseBase != null && t.PulseBase[tl] >= 0)
                cells = Math.Max(cells, (t.PulseBase[tl] + (int)t.Timelines[tl].Duration) * 2);
        var start = (int*)KindStore.Zeroed((nuint)(cells + 1) * 4);
        byte* data = null;
        try
        {
            var vals = new List<T>();
            var feeders = t.Sec.Feeders;
            for (var tl = 0; tl < t.TimelineCount; tl++)
            {
                var pb = t.PulseBase != null ? t.PulseBase[tl] : -1;
                if (pb < 0) continue;
                var id = (ushort)(tl + 1);
                var tk = t.Sec.Timelines + tl;
                var events = t.Sec.Events(tk);
                var dur = (int)t.Timelines[tl].Duration;
                for (var tick = 0; tick < dur; tick++)
                {
                    var bits = t.PulseBits[pb + tick];
                    for (var d = 0; d < 2; d++)
                    {
                        var cell = ((pb + tick) << 1) + d;
                        start[cell] = vals.Count;
                        if ((bits & (1 << d)) == 0 || (t.PulseMasks[cell] >> kind & 1) == 0) continue;
                        var to = t.Sec.EventEnd(tk, tick, d);
                        for (var n = t.Sec.EventStart(tk, tick, d); n < to; n++)
                        {
                            var ev = events[n];
                            if (ev.Op < (byte)KindEventOp.PulseStart) continue;
                            var feeder = feeders + ev.Feeder;
                            if (feeder->Kind != kind) continue;
                            var route = routes[feeder->Ordinal];
                            if ((route >> 8) != part) continue;
                            var step = steps.StepOf(id, ev.Window, (ushort)tick);
                            if (step == null || tick < step->WindowStart || tick >= step->WindowEnd) continue;
                            if (eval.Evaluate(route & 0xff, step, t.Timelines + tl, (ushort)tick, d == 1, out var v)) vals.Add(v);
                        }
                    }
                }
            }
            start[cells] = vals.Count;
            var stride = sizeof(T);
            data = (byte*)KindStore.Zeroed((nuint)Math.Max(1, vals.Count) * (nuint)stride);
            for (var i = 0; i < vals.Count; i++) *(T*)(data + (nuint)i * (nuint)stride) = vals[i];
            return new KindPulseValues
            {
                Start = start,
                Data = data,
                Stride = stride,
                Cells = cells,
                Part = part,
                Bytes = (long)(cells + 1) * 4 + (long)vals.Count * stride,
            };
        }
        catch
        {
            NativeMemory.AlignedFree(start);
            NativeMemory.AlignedFree(data);
            throw;
        }
    }

    internal void Free()
    {
        NativeMemory.AlignedFree(Start);
        NativeMemory.AlignedFree(Data);
        this = default;
    }
}
