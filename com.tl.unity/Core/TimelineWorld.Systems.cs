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
        players.Kinds?.Sync();
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
        var timelines = _view.Timelines;
        if (timelines == null) return;
        var count = (uint)_view.TimelineCount;
        for (var entity = 0; entity < players.EntityCapacity; entity++)
        {
            var slots = players.SlotsOf(entity);
            var slotsCount = players.CountOf(entity);
            if (slotsCount == 0) continue;
            for (var i = 0; i < slotsCount; i++)
            {
                var current = slots + i;
                if (current->Timeline == 0) continue;
                var flags = current->Flags;
                if ((flags & (PlaybackFlags.Ended | PlaybackFlags.Paused)) != 0)
                {
                    current->Flags = flags & ~PlaybackFlags.StartPending;
                    continue;
                }
                if (current->Timeline == 0 || (uint)(current->Timeline - 1) >= count)
                    throw new ArgumentException($"Playback slot {i} of entity {entity} names no timeline in the loaded library.");
                var timeline = timelines + (current->Timeline - 1);
                var duration = timeline->Duration;
                var reverse = (flags & PlaybackFlags.Reverse) != 0;
                var handover = false;
                if (duration == 0)
                {
                    current->Flags = (flags | PlaybackFlags.Ended) & ~PlaybackFlags.StartPending;
                }
                else if (reverse == forward)
                {
                    if (current->Tick == 0)
                    {
                        if (timeline->Loop != 0) current->Tick = (ushort)(duration - 1);
                        else if (timeline->Hold != 0) current->Flags = flags | PlaybackFlags.Ended;
                        else { players.RemoveSlot(entity, i); continue; }
                    }
                    else current->Tick--;
                }
                else if ((uint)current->Tick + 1u >= duration)
                {
                    if (timeline->Loop != 0) current->Tick = 0;
                    else if (timeline->Next != 0 && (uint)(timeline->Next - 1) < count)
                    {
                        var next = timelines + (timeline->Next - 1);
                        current->Timeline = timeline->Next;
                        current->Tick = 0;
                        current->Channel = (byte)next->Channel;
                        handover = true;
                    }
                    else if (timeline->Hold != 0)
                    {
                        current->Tick = (ushort)(duration - 1);
                        current->Flags = flags | PlaybackFlags.Ended;
                    }
                    else { players.RemoveSlot(entity, i); continue; }
                }
                else current->Tick++;
                current->Flags &= ~PlaybackFlags.StartPending;
                if (handover) current->Flags |= PlaybackFlags.StartPending;
            }
        }
        ClosingList.Clear(players.Closing);
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

    unsafe void ApplyRecord(PlaybackTable players, in RequestRecord record)
    {
        if ((uint)record.Entity >= (uint)players.EntityCapacity)
            throw new ArgumentOutOfRangeException(nameof(record), $"Request names entity {record.Entity} outside the table capacity.");
        if (record.Op == RequestOp.Start && (record.Timeline == 0 || (uint)(record.Timeline - 1) >= (uint)_view.TimelineCount))
            throw new ArgumentException($"Request starts timeline {record.Timeline}; the loaded library holds {_view.TimelineCount}.");
        var slots = new PlaybackTable.Slots(players, record.Entity);
        PlaybackRules.Apply(ref slots, _view.Timelines, _view.TimelineCount, record.Op, record.Timeline, record.At);
        players.SetPlaying(record.Entity, true);
    }
}
