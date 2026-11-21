using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct KindPulseHit
{
    public int Entity;
    public int Cell;
}

public unsafe struct KindPulseBuffer
{
    public KindPulseHit* Items;
    public int Count;
    internal int Capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Push(int entity, int cell)
    {
        if (Count == Capacity) Grow();
        Items[Count++] = new KindPulseHit { Entity = entity, Cell = cell };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Grow()
    {
        var next = Capacity == 0 ? 64 : Capacity * 2;
        var block = (KindPulseHit*)NativeMemory.AlignedAlloc((nuint)next * (nuint)sizeof(KindPulseHit), 64);
        if (Items != null)
        {
            if (Count > 0) new ReadOnlySpan<KindPulseHit>(Items, Count).CopyTo(new Span<KindPulseHit>(block, next));
            NativeMemory.AlignedFree(Items);
        }
        Items = block;
        Capacity = next;
    }

    internal void Rewind(int entity)
    {
        while (Count > 0 && Items[Count - 1].Entity == entity) Count--;
    }

    public int LowerBound(int entity)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (Items[mid].Entity < entity) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    internal void Free()
    {
        NativeMemory.AlignedFree(Items);
        this = default;
    }
}

public unsafe struct KindTables
{
    public LibraryTimeline* Timelines;
    public int TimelineCount;
    public KindSectionView Sec;
    public int KindCount;
    public ulong* Closure;
    public uint* Pairs;
    public int* PairStart;
    public uint* SpanMeta;
    public int* PulseBase;
    public byte* PulseBits;
    public ulong* PulseMasks;
    internal KindPulseBuffer* PulseHits;
    internal ulong* EntryPulse;
    public ulong FastPulseMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Reaches(ushort timeline, ulong reach) => KindCount == 0 || (Closure[timeline - 1] & ~reach) == 0;
}

public unsafe interface IKindRows
{
    KindEntry* Append(int row, LibraryFeeder* feeder);
    void Purge(int row, byte slot, ushort timeline);
    Playback* Reserve(int row, int used, int length);
    void Sampled(int row, int samples);
}

public unsafe interface IKindPulseSink
{
    bool Active { get; }
    void Push(in KindTables t, int kind, int row, int cell);
    void Rewind(in KindTables t, int kind, int row);
}

internal unsafe struct NoPulseSink : IKindPulseSink
{
    public bool Active => false;
    public void Push(in KindTables t, int kind, int row, int cell) { }
    public void Rewind(in KindTables t, int kind, int row) { }
}

internal unsafe struct BufferPulseSink : IKindPulseSink
{
    public bool Active => true;
    public void Push(in KindTables t, int kind, int row, int cell) => t.PulseHits[kind].Push(row, cell);
    public void Rewind(in KindTables t, int kind, int row) => t.PulseHits[kind].Rewind(row);
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
            if (feeder->Trigger == 2 && tick < windows[w].End - 1) Append(ref rows, row, feeder, slot, id, w, KindEntryFlags.Pulse | KindEntryFlags.Interrupted);
        }
    }

    public const int MaxSampleSlots = 256;

    public static int Depth(Playback* copies, int samples)
    {
        var depth = 1;
        for (var i = 0; i < samples; i++) depth = Math.Max(depth, copies[i].Phase + 1);
        return depth;
    }

    public static bool Due(KindEntry* entry, int substep, Playback* run, int count) => entry->Timeline != 0 && entry->Slot >= count ? run[entry->Slot].Phase == substep : substep == 0;

    public static bool TickEntity<TRows>(in KindTables t, Playback* slots, int count, ref TRows rows, int row, int stepDir) where TRows : struct, IKindRows
    {
        NoPulseSink none = default;
        return TickEntity(in t, slots, count, ref rows, row, stepDir, ref none);
    }

    public static bool TickEntity<TRows, TPulse>(in KindTables t, Playback* slots, int count, ref TRows rows, int row, int stepDir, ref TPulse pulse) where TRows : struct, IKindRows where TPulse : struct, IKindPulseSink
    {
        if (t.SpanMeta == null || t.PulseBase == null || t.PulseBits == null || t.PulseMasks == null) return TickEntitySlow(in t, slots, count, ref rows, row, stepDir);
        var ep = t.EntryPulse;
        var fastMask = !pulse.Active || (ep != null && (ep[row >> 6] >> (row & 63) & 1) != 0) ? 0ul : t.FastPulseMask;
        var hitFast = 0ul;
        var live = false;
        var meta = t.SpanMeta;
        for (var i = 0; i < count; i++)
        {
            var s = slots + i;
            var id = s->Timeline;
            if (id == 0) continue;
            var flags = s->Flags;
            if ((flags & ~PlaybackFlags.Reverse) == 0 && s->Rate == 0)
            {
                var m = meta[id - 1];
                var dur = m & 0x3FFF_FFFFu;
                var dir = flags == 0 ? stepDir : -stepDir;
                var tick = s->Tick + dir;
                if ((uint)tick >= dur)
                {
                    if ((m & 0x8000_0000u) == 0 || (tick != (int)dur && tick != -1)) goto slow;
                    tick = tick < 0 ? (int)dur - 1 : 0;
                }
                s->Tick = (ushort)tick;
                PulseAt(in t, ref rows, row, i, id, tick, dir, fastMask, ref hitFast, ref pulse);
                live = true;
                continue;
            }
            else if (s->Rate != 0)
            {
                if (fastMask != 0)
                {
                    Backfill(in t, slots, i, ref rows, row, stepDir, ref hitFast, ref pulse);
                    fastMask = 0;
                }
                return TickRatedFrom(in t, slots, count, ref rows, row, i, stepDir, live);
            }
        slow:
            live |= TickSlotCore(in t, slots, i, ref rows, row, s, stepDir, ref fastMask, ref hitFast, ref pulse);
        }
        rows.Sampled(row, 0);
        return live;
    }

    static bool TickEntitySlow<TRows>(in KindTables t, Playback* slots, int count, ref TRows rows, int row, int stepDir) where TRows : struct, IKindRows
    {
        var live = false;
        var tls = t.Timelines;
        ulong zero = 0;
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
            NoPulseSink none = default;
            live |= TickSlotCore(in t, slots, i, ref rows, row, s, stepDir, ref zero, ref zero, ref none);
        }
        rows.Sampled(row, 0);
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
        rows.Sampled(row, samples);
        return live || samples != 0;
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
        var crossings = PlaybackRules.Cross(ref *s);
        var room = MaxSampleSlots - count - samples;
        if (crossings > 0 && room > 0)
        {
            slots = rows.Reserve(row, count + samples, count + samples + Math.Min(crossings, room));
            s = slots + i;
        }
        for (var j = 0; j < crossings; j++)
        {
            var at = j < room ? count + samples : i;
            live = TickSlot(in t, ref rows, row, at, s, stepDir);
            if (at != i)
            {
                slots[at] = *s;
                slots[at].Phase = (byte)j;
                samples++;
            }
            if (!live || s->Timeline == 0 || (s->Flags & (PlaybackFlags.Ended | PlaybackFlags.Paused)) != 0) break;
        }
        return slots;
    }

    public static bool TickSlot<TRows>(in KindTables t, ref TRows rows, int row, int i, Playback* s, int stepDir) where TRows : struct, IKindRows
    {
        ulong zero = 0;
        NoPulseSink none = default;
        return TickSlotCore(in t, null, i, ref rows, row, s, stepDir, ref zero, ref zero, ref none);
    }

    static bool TickSlotCore<TRows, TPulse>(in KindTables t, Playback* slots, int i, ref TRows rows, int row, Playback* s, int stepDir, ref ulong fastMask, ref ulong hitFast, ref TPulse pulse) where TRows : struct, IKindRows where TPulse : struct, IKindPulseSink
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
            if (fastMask != 0)
            {
                Backfill(in t, slots, i, ref rows, row, stepDir, ref hitFast, ref pulse);
                fastMask = 0;
            }
            Begin(in t, ref rows, row, i, s, stepDir);
            return true;
        }
        if ((s->Flags & PlaybackFlags.ReenterPending) != 0)
        {
            s->Flags |= PlaybackFlags.Began;
            return true;
        }
        if ((s->Flags & PlaybackFlags.Paused) != 0) return false;
        if ((s->Flags & PlaybackFlags.Ended) != 0) return false;
        var before = *s;
        var motion = PlaybackRules.Move(ref *s, t.Timelines, t.TimelineCount, stepDir > 0);
        var dir = (before.Flags & PlaybackFlags.Reverse) != 0 ? -stepDir : stepDir;
        switch (motion)
        {
            case PlaybackMotion.Stepped:
            case PlaybackMotion.Wrapped:
                if (t.PulseBase != null && t.PulseBits != null && t.PulseMasks != null) PulseAt(in t, ref rows, row, i, before.Timeline, s->Tick, dir, fastMask, ref hitFast, ref pulse);
                else Events(in t, ref rows, row, i, before.Timeline, s->Tick, dir);
                return true;
            case PlaybackMotion.Handed:
                if (fastMask != 0)
                {
                    Backfill(in t, slots, i, ref rows, row, stepDir, ref hitFast, ref pulse);
                    fastMask = 0;
                }
                Begin(in t, ref rows, row, i, s, stepDir);
                return true;
            case PlaybackMotion.Parked:
                return true;
            case PlaybackMotion.Removed:
            case PlaybackMotion.Unknown:
                *s = default;
                return false;
            case PlaybackMotion.Held:
            default:
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PulseAt<TRows, TPulse>(in KindTables t, ref TRows rows, int row, int slot, ushort id, int tick, int dir, ulong fastMask, ref ulong hitFast, ref TPulse pulse) where TRows : struct, IKindRows where TPulse : struct, IKindPulseSink
    {
        var pb = t.PulseBase[id - 1];
        if (pb < 0) return;
        var d = dir > 0 ? 0 : 1;
        if ((t.PulseBits[pb + tick] & (1 << d)) == 0) return;
        var cell = ((pb + tick) << 1) + d;
        RouteEvents(in t, ref rows, row, slot, id, tick, dir, cell, t.PulseMasks[cell], fastMask, ref hitFast, ref pulse);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Events<TRows>(in KindTables t, ref TRows rows, int row, int i, ushort id, int tick, int dir) where TRows : struct, IKindRows
    {
        if (t.KindCount != 0 && t.Sec.Timelines[id - 1].KindMask != 0 && (t.Sec.EventBitsAt(t.Sec.Timelines + (id - 1), tick) & (dir > 0 ? 1 : 2)) != 0) ApplyEventsFiltered(in t, ref rows, row, i, id, tick, dir);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void RouteEvents<TRows, TPulse>(in KindTables t, ref TRows rows, int row, int slot, ushort id, int tick, int dir, int cell, ulong km, ulong fastMask, ref ulong hitFast, ref TPulse pulse) where TRows : struct, IKindRows where TPulse : struct, IKindPulseSink
    {
        var fast = pulse.Active ? km & fastMask : 0ul;
        if (fast != 0)
        {
            hitFast |= fast;
            do
            {
                pulse.Push(in t, KindStore.Tz(fast), row, cell);
                fast &= fast - 1;
            } while (fast != 0);
        }
        var slow = km & ~fastMask;
        if (slow != 0) ApplyEventsFiltered(in t, ref rows, row, slot, id, tick, dir, slow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Backfill<TRows, TPulse>(in KindTables t, Playback* slots, int upto, ref TRows rows, int row, int stepDir, ref ulong hitFast, ref TPulse pulse) where TRows : struct, IKindRows where TPulse : struct, IKindPulseSink
    {
        if (!pulse.Active || hitFast == 0) return;
        for (var m = hitFast; m != 0; m &= m - 1) pulse.Rewind(in t, KindStore.Tz(m), row);
        if (slots != null)
            for (var j = 0; j < upto; j++)
            {
                var p = slots + j;
                var pid = p->Timeline;
                if (pid == 0 || (p->Flags & ~PlaybackFlags.Reverse) != 0 || p->Rate != 0) continue;
                var pb = t.PulseBase[pid - 1];
                if (pb < 0) continue;
                var dir = (p->Flags & PlaybackFlags.Reverse) == 0 ? stepDir : -stepDir;
                var d = dir > 0 ? 0 : 1;
                int tick = p->Tick;
                if ((t.PulseBits[pb + tick] & (1 << d)) == 0) continue;
                var km = t.PulseMasks[((pb + tick) << 1) + d] & hitFast;
                if (km != 0) ApplyEventsFiltered(in t, ref rows, row, j, pid, tick, dir, km);
            }
        hitFast = 0;
    }

    static void Begin<TRows>(in KindTables t, ref TRows rows, int row, int i, Playback* s, int stepDir) where TRows : struct, IKindRows
    {
        s->Flags = (s->Flags & ~PlaybackFlags.StartPending) | PlaybackFlags.Began;
        var id = s->Timeline;
        if (t.KindCount == 0 || t.Sec.Timelines[id - 1].KindMask == 0) return;
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

    static void ApplyEventsFiltered<TRows>(in KindTables t, ref TRows rows, int row, int i, ushort id, int tick, int dir, ulong kindMask = ~0ul) where TRows : struct, IKindRows
    {
        var tk = t.Sec.Timelines + (id - 1);
        var events = t.Sec.Events(tk);
        var d = dir > 0 ? 0 : 1;
        var to = t.Sec.EventEnd(tk, tick, d);
        for (var n = t.Sec.EventStart(tk, tick, d); n < to; n++)
        {
            var ev = events[n];
            var feeder = t.Sec.Feeders + ev.Feeder;
            if (ev.Op >= (byte)KindEventOp.PulseStart && (kindMask >> feeder->Kind & 1) != 0) Append(ref rows, row, feeder, i, id, ev.Window, KindEntryFlags.Pulse);
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
