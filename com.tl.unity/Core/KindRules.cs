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
    void Purge(int row, byte slot, ushort timeline);
    Playback* Reserve(int row, int used, int length);
    void Sample(int row, byte slot, byte sample);
    void Unsample(int row);
}

public static unsafe class KindRules
{
    public static bool Rebind<TRows>(in KindTables t, ref TRows rows, int row, ref Playback s, int slot) where TRows : struct, IKindRows
    {
        if (s.Timeline == 0) return false;
        rows.Purge(row, (byte)slot, s.Timeline);
        return PlaybackRules.Rebind(ref s, t.Timelines, t.TimelineCount);
    }

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

    public const byte Marker = 255;
    public const int MaxSampleSlots = 256;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSampled(byte* data, int length) => length != 0 && (((KindEntry*)data)->Flags & KindEntryFlags.Sample) != 0;

    public static int SampleDepth(byte* data, int length, int stride, Playback* slots)
    {
        var depth = 1;
        for (var i = 0; i < length; i++)
        {
            var e = (KindEntry*)(data + (nuint)i * (nuint)stride);
            if ((e->Flags & KindEntryFlags.Sample) == 0) break;
            var j = slots[e->Slot].Phase;
            if (j != Marker && j + 1 > depth) depth = j + 1;
        }
        return depth;
    }

    public static bool Due(KindEntry* entry, int substep, Playback* slot)
    {
        if ((entry->Flags & KindEntryFlags.Sample) != 0) return slot->Phase == substep;
        return substep == 0 && (entry->Timeline == 0 || (entry->Flags & KindEntryFlags.Pulse) != 0 || slot->Rate == 0);
    }

    public static bool TickEntity<TRows>(in KindTables t, Playback* slots, int count, ref TRows rows, int row, int stepDir) where TRows : struct, IKindRows
    {
        var live = false;
        var tls = t.Timelines;
        for (var i = 0; i < count; i++)
        {
            var s = slots + i;
            var id = s->Timeline;
            if (id == 0) continue;
            var flags = s->Flags;
            if ((flags & ~PlaybackFlags.Reverse) == 0 && s->Rate == 0)
            {
                var dir = flags == 0 ? stepDir : -stepDir;
                var tick = s->Tick + dir;
                if ((uint)tick < tls[id - 1].Duration)
                {
                    s->Tick = (ushort)tick;
                    Events(in t, ref rows, row, i, id, tick, dir);
                    live = true;
                    continue;
                }
            }
            else if (s->Rate != 0) return TickRatedFrom(in t, slots, count, ref rows, row, i, stepDir, live);
            live |= TickSlot(in t, ref rows, row, i, s, stepDir);
        }
        return live;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool TickRatedFrom<TRows>(in KindTables t, Playback* slots, int count, ref TRows rows, int row, int from, int stepDir, bool live) where TRows : struct, IKindRows
    {
        var samples = 0;
        for (var i = from; i < count; i++)
        {
            if (slots[i].Timeline == 0) continue;
            if (slots[i].Rate == 0) live |= TickSlot(in t, ref rows, row, i, slots + i, stepDir);
            else
            {
                slots = TickRated(in t, slots, count, ref rows, row, i, stepDir, ref samples, out var rated);
                live |= rated;
            }
        }
        return live;
    }

    static Playback* TickRated<TRows>(in KindTables t, Playback* slots, int count, ref TRows rows, int row, int i, int stepDir, ref int samples, out bool live) where TRows : struct, IKindRows
    {
        var s = slots + i;
        live = true;
        if (!PlaybackRules.Moves(s->Flags, stepDir > 0))
        {
            live = TickSlot(in t, ref rows, row, i, s, stepDir);
            return slots;
        }
        if (samples == 0) rows.Unsample(row);
        var crossings = PlaybackRules.Cross(ref *s);
        var room = MaxSampleSlots - count - samples;
        if (room <= 0) return slots;
        if (crossings > room) crossings = room;
        slots = rows.Reserve(row, count + samples, count + samples + (crossings == 0 ? 1 : crossings));
        s = slots + i;
        if (crossings == 0)
        {
            var mark = count + samples++;
            slots[mark] = *s;
            slots[mark].Phase = Marker;
            rows.Sample(row, (byte)i, (byte)mark);
            return slots;
        }
        for (var j = 0; j < crossings; j++)
        {
            live = TickSlot(in t, ref rows, row, i, s, stepDir);
            var at = count + samples++;
            slots[at] = *s;
            slots[at].Phase = (byte)j;
            rows.Sample(row, (byte)i, (byte)at);
            if (!live || s->Timeline == 0 || (s->Flags & (PlaybackFlags.Ended | PlaybackFlags.Paused)) != 0) break;
        }
        return slots;
    }

    static bool TickSlot<TRows>(in KindTables t, ref TRows rows, int row, int i, Playback* s, int stepDir) where TRows : struct, IKindRows
    {
        if ((s->Flags & (PlaybackFlags.Began | PlaybackFlags.ReenterPending)) == (PlaybackFlags.Began | PlaybackFlags.ReenterPending)) s->Flags &= ~PlaybackFlags.ReenterPending;
        s->Flags &= ~PlaybackFlags.Began;
        if ((s->Flags & PlaybackFlags.Stopping) != 0)
        {
            if ((s->Flags & PlaybackFlags.StopAged) != 0)
            {
                *s = default;
                return false;
            }
            s->Flags |= PlaybackFlags.StopAged;
            return true;
        }
        if (PlaybackRules.IsParked(s->Flags) && !PlaybackRules.Unpark(ref *s, stepDir > 0)) return true;
        if ((s->Flags & PlaybackFlags.StartPending) != 0)
        {
            Begin(in t, ref rows, row, i, s, stepDir);
            return true;
        }
        if ((s->Flags & PlaybackFlags.ReenterPending) != 0)
        {
            s->Flags |= PlaybackFlags.Began;
            Reenter(in t, ref rows, row, in *s, i);
            return true;
        }
        if ((s->Flags & PlaybackFlags.Paused) != 0) return true;
        if ((s->Flags & PlaybackFlags.Ended) != 0) return false;
        var dir = (s->Flags & PlaybackFlags.Reverse) != 0 ? -stepDir : stepDir;
        var id = s->Timeline;
        var tl = t.Timelines + (id - 1);
        var duration = (int)tl->Duration;
        var tick = s->Tick + dir;
        if ((uint)tick < (uint)duration)
        {
            s->Tick = (ushort)tick;
            Events(in t, ref rows, row, i, id, tick, dir);
            return true;
        }
        if (tl->Loop != 0)
        {
            WrapRemove(in t, ref rows, row, i, id, dir);
            tick = dir > 0 ? 0 : duration - 1;
            s->Tick = (ushort)tick;
            Events(in t, ref rows, row, i, id, tick, dir);
            return true;
        }
        RemoveActiveSpans(in t, ref rows, row, i, s);
        if (tick < 0 && stepDir > 0)
        {
            s->Tick = 0;
            s->Flags |= PlaybackRules.Parked;
            return true;
        }
        if (tl->Next != 0)
        {
            var next = t.Timelines + (tl->Next - 1);
            s->Timeline = tl->Next;
            s->Channel = (byte)next->Channel;
            s->Tick = (ushort)(dir > 0 ? 0 : next->Duration - 1);
            Begin(in t, ref rows, row, i, s, stepDir);
            return true;
        }
        if (tl->Hold != 0)
        {
            s->Tick = (ushort)(duration - 1);
            s->Flags |= PlaybackFlags.Ended;
            return false;
        }
        *s = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Events<TRows>(in KindTables t, ref TRows rows, int row, int i, ushort id, int tick, int dir) where TRows : struct, IKindRows
    {
        if (t.KindCount != 0 && t.Sec.Timelines[id - 1].KindMask != 0 && (t.Sec.EventBitsAt(t.Sec.Timelines + (id - 1), tick) & (dir > 0 ? 1 : 2)) != 0) ApplyEvents(in t, ref rows, row, i, id, tick, dir);
    }

    static void Begin<TRows>(in KindTables t, ref TRows rows, int row, int i, Playback* s, int stepDir) where TRows : struct, IKindRows
    {
        s->Flags = (s->Flags & ~PlaybackFlags.StartPending) | PlaybackFlags.Began;
        var id = s->Timeline;
        if (t.KindCount == 0 || t.Sec.Timelines[id - 1].KindMask == 0) return;
        var dir = (s->Flags & PlaybackFlags.Reverse) != 0 ? -stepDir : stepDir;
        var windows = t.Sec.Windows(t.Sec.Timelines + (id - 1));
        int tick = s->Tick;
        for (var k = t.PairStart[id - 1]; k < t.PairStart[id]; k++)
        {
            var w = (int)(t.Pairs[k] >> 16);
            var window = windows[w];
            var feeder = t.Sec.Feeders + (t.Pairs[k] & 0xffff);
            var inside = tick >= window.Start && tick < window.End;
            switch (feeder->Trigger)
            {
                case 0:
                    if (inside) Append(ref rows, row, feeder, i, id, w, KindEntryFlags.Span | ((dir > 0 ? tick != window.Start : tick != window.End - 1) ? KindEntryFlags.Synthetic : 0));
                    break;
                case 1:
                    if (inside) Append(ref rows, row, feeder, i, id, w, KindEntryFlags.Pulse | (tick != window.Start ? KindEntryFlags.Synthetic : 0));
                    break;
                case 2:
                    if (tick == window.End - 1) Append(ref rows, row, feeder, i, id, w, KindEntryFlags.Pulse);
                    break;
                default:
                    if (tick == window.Start) Append(ref rows, row, feeder, i, id, w, KindEntryFlags.Pulse);
                    break;
            }
        }
    }

    public static void Reenter<TRows>(in KindTables t, ref TRows rows, int row, in Playback s, int slot) where TRows : struct, IKindRows
    {
        var id = s.Timeline;
        if (t.KindCount == 0 || t.Sec.Timelines[id - 1].KindMask == 0) return;
        var windows = t.Sec.Windows(t.Sec.Timelines + (id - 1));
        int tick = s.Tick;
        for (var k = t.PairStart[id - 1]; k < t.PairStart[id]; k++)
        {
            var w = (int)(t.Pairs[k] >> 16);
            var feeder = t.Sec.Feeders + (t.Pairs[k] & 0xffff);
            if (feeder->Trigger == 0 && tick >= windows[w].Start && tick < windows[w].End) Append(ref rows, row, feeder, slot, id, w, KindEntryFlags.Span | KindEntryFlags.Synthetic);
        }
    }

    static void RemoveActiveSpans<TRows>(in KindTables t, ref TRows rows, int row, int i, Playback* s) where TRows : struct, IKindRows
    {
        var id = s->Timeline;
        if (t.KindCount == 0 || t.Sec.Timelines[id - 1].KindMask == 0) return;
        var windows = t.Sec.Windows(t.Sec.Timelines + (id - 1));
        int tick = s->Tick;
        for (var k = t.PairStart[id - 1]; k < t.PairStart[id]; k++)
        {
            var w = (int)(t.Pairs[k] >> 16);
            var feeder = t.Sec.Feeders + (t.Pairs[k] & 0xffff);
            if (feeder->Trigger == 0 && tick >= windows[w].Start && tick < windows[w].End) rows.RemoveSpan(row, feeder->Kind, (byte)i, id, (ushort)w);
        }
    }

    static void WrapRemove<TRows>(in KindTables t, ref TRows rows, int row, int i, ushort id, int dir) where TRows : struct, IKindRows
    {
        if (t.KindCount == 0) return;
        var tk = t.Sec.Timelines + (id - 1);
        if (tk->KindMask == 0) return;
        var events = t.Sec.Events(tk);
        var from = dir > 0 ? tk->WrapForward : tk->WrapBackward;
        var to = from + (dir > 0 ? tk->WrapForwardCount : tk->WrapBackwardCount);
        for (var n = from; n < to; n++)
        {
            var feeder = t.Sec.Feeders + events[n].Feeder;
            rows.RemoveSpan(row, feeder->Kind, (byte)i, id, events[n].Window);
        }
    }

    static void ApplyEvents<TRows>(in KindTables t, ref TRows rows, int row, int i, ushort id, int tick, int dir) where TRows : struct, IKindRows
    {
        var tk = t.Sec.Timelines + (id - 1);
        var events = t.Sec.Events(tk);
        var d = dir > 0 ? 0 : 1;
        var to = t.Sec.EventEnd(tk, tick, d);
        for (var n = t.Sec.EventStart(tk, tick, d); n < to; n++)
        {
            var ev = events[n];
            var feeder = t.Sec.Feeders + ev.Feeder;
            switch ((KindEventOp)ev.Op)
            {
                case KindEventOp.Remove:
                    rows.RemoveSpan(row, feeder->Kind, (byte)i, id, ev.Window);
                    break;
                case KindEventOp.Insert:
                    Append(ref rows, row, feeder, i, id, ev.Window, KindEntryFlags.Span);
                    break;
                default:
                    Append(ref rows, row, feeder, i, id, ev.Window, KindEntryFlags.Pulse);
                    break;
            }
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
