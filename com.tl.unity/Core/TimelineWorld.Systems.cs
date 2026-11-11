using System;

namespace Tl;

public sealed partial class TimelineWorld
{
    internal SystemRegistry? _systems;
    internal ulong _generation;

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
        Drain(players, players.Consumer);
        Drain(players, players.Pending);
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
        AdvanceRange(players, timelines, count, forward, 0, players.PlayingWords);
        ClosingList.Clear(players.Closing);
    }


    unsafe void AdvanceRange(PlaybackTable players, LibraryTimeline* timelines, int count, bool forward, int firstWord, int endWord)
    {
        var playing = players.Playing;
        if (firstWord < 0) firstWord = 0; if (endWord > players.PlayingWords) endWord = players.PlayingWords;
        for (var w = firstWord; w < endWord; w++)
        {
            var bits = playing[w];
            while (bits != 0)
            {
                var entity = (w << 6) + KindStore.Tz(bits);
                bits &= bits - 1;
                AdvanceEntity(players, entity, timelines, count, forward);
            }
        }
    }

    unsafe void AdvanceEntity(PlaybackTable players, int entity, LibraryTimeline* timelines, int count, bool forward)
    {
        var slots = players.SlotsOf(entity);
        var slotsCount = players.CountOf(entity);
        // One pass: step every slot and decide in the same visit whether the entity keeps playing. A removal zeroes its slot and keeps
        // the other indices stable, and a later slot never changes an earlier slot's flags, so this equals stepping all slots first and scanning them after.
        var holds = false;
        for (var i = 0; i < slotsCount; i++)
        {
            if (slots[i].Timeline == 0) continue;
            var result = PlaybackRules.Step(ref slots[i], timelines, count, forward);
            if (result == AdvanceResult.Removed) { players.RemoveSlot(entity, i); slots = players.SlotsOf(entity); continue; }
            if (result == AdvanceResult.Unknown) throw new ArgumentException($"Playback slot {i} of entity {entity} names no timeline in the loaded library.");
            if (holds) continue;
            var f = slots[i].Flags;
            if (PlaybackRules.Moves(f, forward) || PlaybackRules.IsParked(f) || (f & (PlaybackFlags.Stopping | PlaybackFlags.ReenterPending)) != 0 || ((f & (PlaybackFlags.Paused | PlaybackFlags.Ended)) != 0 && (f & PlaybackFlags.StartPending) != 0))
                holds = true;
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
        for (var i = 0; i < queue->Count; i++)
        {
            ref readonly var record = ref queue->Item(i);
            ApplyRecord(players, record);
        }
        RequestQueue.Clear(queue);
    }

    unsafe void SyncPlaybacks(PlaybackTable players)
    {
        if (players.LibraryGeneration == _generation) return;
        for (var entity = 0; entity < players.EntityCapacity; entity++)
        {
            var slots = players.SlotsOf(entity);
            var count = players.CountOf(entity);
            for (var slot = 0; slot < count; slot++)
            {
                if (slots[slot].Timeline > _view.TimelineCount && (slots[slot].Flags & PlaybackFlags.Stopping) == 0)
                    throw new ArgumentException($"Playback slot {slot} of entity {entity} names no timeline in the reloaded library.");
                if (!PlaybackRules.Rebind(ref slots[slot], _view.Timelines, _view.TimelineCount)) { players.RemoveSlot(entity, slot); slots = players.SlotsOf(entity); }
            }
        }
        players.LibraryGeneration = _generation;
    }

    unsafe void ApplyRecord(PlaybackTable players, in RequestRecord record)
    {
        if ((uint)record.Entity >= (uint)players.EntityCapacity)
            throw new ArgumentOutOfRangeException(nameof(record), $"Request names entity {record.Entity} outside the table capacity.");
        if (record.Op == RequestOp.Start && (record.Timeline == 0 || (uint)(record.Timeline - 1) >= (uint)_view.TimelineCount))
            throw new ArgumentException($"Request starts timeline {record.Timeline}; the loaded library holds {_view.TimelineCount}.");
        if (record.Op == RequestOp.Reset)
        {
            players.ResetEntity(record.Entity);
            return;
        }
        var slots = new PlaybackTable.Slots(players, record.Entity);
        PlaybackRules.Apply(ref slots, _view.Timelines, _view.TimelineCount, record.Op, record.Timeline, record.At);
        players.SetPlaying(record.Entity, true);
    }
}
