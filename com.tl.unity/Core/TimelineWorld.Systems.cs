using System;
#if NET
using System.Runtime.Intrinsics.X86;
#endif

namespace Tl;

public sealed partial class TimelineWorld
{
    internal SystemRegistry? _systems;
    internal ulong _generation;
    bool _glideViewEscaped;

    public unsafe Requests Requests(PlaybackTable players)
    {
        if (players is null) throw new ArgumentNullException(nameof(players));
        return new Requests(players.Consumer, -1, -1);
    }

    public unsafe void ApplyRequests(PlaybackTable players)
    {
        if (players is null) throw new ArgumentNullException(nameof(players));
        if (players.Kinds != null) players.Kinds.Sync();
        else SyncPlaybacks(players);
        var bad = Invalid(players, players.Consumer) ?? Invalid(players, players.Pending);
        if (bad != null)
        {
            RequestQueue.Clear(players.Consumer);
            RequestQueue.Clear(players.Pending);
            throw bad;
        }
        Drain(players, players.Consumer);
        Drain(players, players.Pending);
    }

    unsafe Exception? Invalid(PlaybackTable players, RequestQueue* queue)
    {
        for (var i = 0; i < queue->Count; i++)
        {
            ref readonly var record = ref queue->Item(i);
            if ((uint)record.Entity >= (uint)players.EntityCapacity)
                return new ArgumentOutOfRangeException(nameof(record), $"Request names entity {record.Entity} outside the table capacity.");
            if (record.Op == RequestOp.Start && (record.Timeline == 0 || (uint)(record.Timeline - 1) >= (uint)_view.TimelineCount))
                return new ArgumentException($"Request starts timeline {record.Timeline}; the loaded library holds {_view.TimelineCount}.");
#if TL_CHECKED
            if (record.Op == RequestOp.Start && players.Kinds?.Unreachable(record.Entity, record.Timeline) is { } unreachable) return unreachable;
#endif
        }
        return null;
    }

    public unsafe void Tick(PlaybackTable players, bool forward = true)
    {
        if (players is null) throw new ArgumentNullException(nameof(players));
        var kinds = players.Kinds ?? throw new InvalidOperationException("Tick needs a KindStore attached to the PlaybackTable; use Advance for kind-free playback.");
        ClosingList.Clear(players.Closing);
        ApplyRequests(players);
        kinds.TickPass(forward);
    }

    public unsafe void Advance(PlaybackTable players, bool forward = true)
    {
        if (players is null) throw new ArgumentNullException(nameof(players));
        if (players.Kinds != null) throw new InvalidOperationException("A PlaybackTable with a KindStore advances through Tick, not Advance.");
        SyncPlaybacks(players);
        var timelines = _view.Timelines;
        if (timelines == null) return;
        var count = _view.TimelineCount;
        if (players.Lazy(_glideViewEscaped ? null : this, forward)) AdvanceLazy(players, timelines, count, forward);
        else
            for (var w = 0; w < players.PlayingWords; w++)
                for (var bits = players.PlayingBits[w]; bits != 0; bits &= bits - 1)
                    AdvanceEntity(players, (w << 6) + KindStore.Tz(bits), timelines, count, forward);
        ClosingList.Clear(players.Closing);
    }

    unsafe void AdvanceLazy(PlaybackTable players, LibraryTimeline* timelines, int count, bool forward)
    {
        const int Block = 64;
        var tick = players.Clock + 1;
        var n = players.EntityCapacity;
        var found = stackalloc ulong[Block];
        var hits = stackalloc ulong[Block + PlaybackTable.MaxSlots];
        var held = 0;
        for (var first = 0; first < n; first += Block)
        {
            var due = 0;
            LazyTicks.Scan(players.Next + first, Math.Min(Block, n - first), tick, found, ref due, (ulong)first);
            for (var k = 0; k < due; k++)
            {
                var e = (int)found[k];
                if ((players.PlayingBits[e >> 6] >> e & 1) == 0) continue;
                var from = held;
                players.Next[e] = (ushort)(tick + 1 + LazyTicks.Scan(players.DueOf(e), players.CountOf(e), tick, hits, ref held, (ulong)e << 16));
                if (from == held) hits[held++] = (ulong)e << 16;
#if NET
                else if (Sse.IsSupported) for (var slots = players.SlotsOf(e); from < held; from++) Sse.Prefetch0(slots + (ushort)hits[from]);
#endif
#if TL_CHECKED
                Checked.Visited();
#endif
                if (held < Block) continue;
                Land(players, hits, held, timelines, count, forward, tick);
                held = 0;
            }
        }
        Land(players, hits, held, timelines, count, forward, tick);
        players.Clock = (ushort)tick;
    }

    unsafe void Land(PlaybackTable players, ulong* hits, int held, LibraryTimeline* timelines, int count, bool forward, int tick)
    {
#if !NET
        for (var k = 0; k < held; k++) System.Threading.Volatile.Read(ref players.SlotsOf((int)(hits[k] >> 16))[(ushort)hits[k]].Tick);
#endif
        for (var k = 0; k < held; k++)
        {
            var entity = (int)(hits[k] >> 16);
            var i = (ushort)hits[k];
            var slots = players.SlotsOf(entity);
            var n = players.CountOf(entity);
            if (i < n && slots[i].Timeline != 0)
            {
                LazyTicks.Shift(slots + i, 1, tick - 1, forward);
                var result = PlaybackRules.Step(ref slots[i], timelines, count, forward);
                if (result == AdvanceResult.Unknown) { players.Thaw(entity, i); throw Lost(i, entity); }
                if (result == AdvanceResult.Removed) { players.RemoveSlot(entity, i); slots = players.SlotsOf(entity); n = players.CountOf(entity); }
            }
            if (i < n)
            {
                var due = LazyTicks.Settle(slots + i, timelines, count, tick, forward);
                players.DueOf(entity)[i] = due;
                if ((ushort)(due - tick - 1) < (ushort)(players.Next[entity] - tick - 1)) players.Next[entity] = due;
                if (slots[i].Timeline != 0 && Holds(slots[i].Flags, forward)) continue;
            }
            var at = 0;
            while (at < n && (slots[at].Timeline == 0 || !Holds(slots[at].Flags, forward))) at++;
            if (at == n) players.SetPlaying(entity, false);
        }
    }

    static Exception Lost(int slot, int entity) => new ArgumentException($"Playback slot {slot} of entity {entity} names no timeline in the loaded library.");

    static bool Holds(PlaybackFlags f, bool forward)
        => PlaybackRules.Moves(f, forward) || PlaybackRules.IsParked(f) || (f & (PlaybackFlags.Stopping | PlaybackFlags.ReenterPending)) != 0 || ((f & (PlaybackFlags.Paused | PlaybackFlags.Ended)) != 0 && (f & PlaybackFlags.StartPending) != 0);

    unsafe void AdvanceEntity(PlaybackTable players, int entity, LibraryTimeline* timelines, int count, bool forward)
    {
        var slots = players.SlotsOf(entity);
        var slotsCount = players.CountOf(entity);
        var holds = false;
        for (var i = 0; i < slotsCount; i++)
        {
            if (slots[i].Timeline == 0) continue;
            var result = PlaybackRules.Step(ref slots[i], timelines, count, forward);
            if (result == AdvanceResult.Removed) { players.RemoveSlot(entity, i); slots = players.SlotsOf(entity); continue; }
            if (result == AdvanceResult.Unknown) throw Lost(i, entity);
            holds = holds || Holds(slots[i].Flags, forward);
        }
        if (!holds) players.SetPlaying(entity, false);
#if TL_CHECKED
        Checked.Visited();
#endif
    }

    unsafe void Drain(PlaybackTable players, RequestQueue* queue)
    {
        if (queue->Count == 0) return;
        RequestQueue.SortStable(queue);
        for (var i = 0; i < queue->Count; i++) ApplyRecord(players, queue->Item(i));
        RequestQueue.Clear(queue);
    }

    unsafe void SyncPlaybacks(PlaybackTable players)
    {
        if (players.LibraryGeneration == _generation) return;
        players.Thaw();
        for (var pass = 0; pass < 2; pass++)
            for (var entity = 0; entity < players.EntityCapacity; entity++)
            {
                var slots = players.SlotsOf(entity);
                var count = players.CountOf(entity);
                for (var slot = 0; slot < count; slot++)
                {
                    if (pass == 0)
                    {
                        if (slots[slot].Timeline > _view.TimelineCount && (slots[slot].Flags & PlaybackFlags.Stopping) == 0)
                            throw new ArgumentException($"Playback slot {slot} of entity {entity} names no timeline in the reloaded library.");
                    }
                    else if (!PlaybackRules.Rebind(ref slots[slot], _view.Timelines, _view.TimelineCount)) { players.RemoveSlot(entity, slot); slots = players.SlotsOf(entity); }
                }
            }
        players.LibraryGeneration = _generation;
    }

    unsafe void ApplyRecord(PlaybackTable players, in RequestRecord record)
    {
        players.Edit(record.Entity);
        try
        {
            if (record.Op == RequestOp.Reset) { players.ResetEntity(record.Entity); return; }
            var slots = new PlaybackTable.Slots(players, record.Entity);
            PlaybackRules.Apply(ref slots, _view.Timelines, _view.TimelineCount, record.Op, record.Timeline, record.At);
            players.SetPlaying(record.Entity, true);
        }
        finally { players.Seal(record.Entity); }
    }
}
