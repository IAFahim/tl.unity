using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    public struct RequestEntry
    {
        public int Entity;
        public int Slot;
        public ushort Timeline;
        public ushort At;
        public global::Tl.RequestOp Op;
        public int Sequence;
    }

    public static unsafe class TimelineKernel
    {
        public const int MaxSlotsPerEntity = 64;

        public static global::Tl.LibraryTimeline* GetTimeline(in global::Tl.LibraryView view, ushort timeline)
        {
            return global::Tl.LibraryViewReader.GetTimeline(in view, new global::Tl.TimelineId(timeline));
        }

        public static bool AdvancePlayback(ref Playback slot, in global::Tl.LibraryView view, bool forward, out bool handover, out bool removed)
        {
            handover = false;
            removed = false;
            var flags = slot.Flags;
            if ((flags & (global::Tl.PlaybackFlags.Ended | global::Tl.PlaybackFlags.Paused)) != 0)
            {
                slot.Flags = flags & ~global::Tl.PlaybackFlags.StartPending;
                return true;
            }
            var tl = GetTimeline(in view, slot.Timeline);
            if (tl == null)
                return false;
            var duration = tl->Duration;
            if (duration == 0)
            {
                slot.Flags = (flags | global::Tl.PlaybackFlags.Ended) & ~global::Tl.PlaybackFlags.StartPending;
                return true;
            }
            var reverse = (flags & global::Tl.PlaybackFlags.Reverse) != 0;
            if (reverse == forward)
            {
                if (slot.Tick == 0)
                {
                    if (tl->Loop != 0)
                        slot.Tick = (ushort)(duration - 1);
                    else if (tl->Hold != 0)
                        slot.Flags = flags | global::Tl.PlaybackFlags.Ended;
                    else
                    {
                        removed = true;
                        return true;
                    }
                }
                else
                    slot.Tick--;
            }
            else if ((uint)slot.Tick + 1u >= duration)
            {
                if (tl->Loop != 0)
                    slot.Tick = 0;
                else if (tl->Next != 0)
                {
                    slot.Timeline = tl->Next;
                    slot.Tick = 0;
                    handover = true;
                }
                else if (tl->Hold != 0)
                {
                    slot.Tick = (ushort)(duration - 1);
                    slot.Flags = flags | global::Tl.PlaybackFlags.Ended;
                }
                else
                {
                    removed = true;
                    return true;
                }
            }
            if (handover)
                slot.Flags |= global::Tl.PlaybackFlags.StartPending;
            return true;
        }

        public static bool ApplyStart(ref DynamicBuffer<Playback> playbacks, int entity, ushort timeline, ushort at, in global::Tl.LibraryView view, ref NativeList<ClosedPlayback> closed, out bool replaced)
        {
            replaced = false;
            var tl = GetTimeline(in view, timeline);
            if (timeline == 0 || tl == null || tl->Duration == 0)
                return false;
            var duration = tl->Duration;
            var clamped = tl->Loop != 0 ? (ushort)(at % duration) : at < duration ? at : (ushort)(duration - 1);
            var channel = (byte)tl->Channel;
            if (channel != 0)
            {
                for (var i = 0; i < playbacks.Length; i++)
                {
                    if (playbacks[i].Channel == channel)
                    {
                        closed.Add(new ClosedPlayback { Entity = entity, Slot = i, Value = playbacks[i] });
                        playbacks.RemoveAt(i);
                        replaced = true;
                        break;
                    }
                }
            }
            for (var i = 0; i < playbacks.Length; i++)
                if (playbacks[i].Timeline == timeline)
                    return false;
            if (playbacks.Length >= MaxSlotsPerEntity)
                return false;
            playbacks.Add(new Playback
            {
                Timeline = timeline,
                Tick = clamped,
                Channel = channel,
                Flags = clamped > 0 ? global::Tl.PlaybackFlags.StartPending : global::Tl.PlaybackFlags.None,
            });
            return true;
        }

        public static bool ApplyStop(ref DynamicBuffer<Playback> playbacks, int entity, ushort timeline, ref NativeList<ClosedPlayback> closed)
        {
            for (var i = 0; i < playbacks.Length; i++)
            {
                if (playbacks[i].Timeline == timeline)
                {
                    closed.Add(new ClosedPlayback { Entity = entity, Slot = i, Value = playbacks[i] });
                    playbacks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public static bool ApplyStopChannel(ref DynamicBuffer<Playback> playbacks, int entity, byte channel, ref NativeList<ClosedPlayback> closed)
        {
            for (var i = 0; i < playbacks.Length; i++)
            {
                if (playbacks[i].Channel == channel)
                {
                    closed.Add(new ClosedPlayback { Entity = entity, Slot = i, Value = playbacks[i] });
                    playbacks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public static void ApplyStopAll(ref DynamicBuffer<Playback> playbacks, int entity, ref NativeList<ClosedPlayback> closed)
        {
            for (var i = 0; i < playbacks.Length; i++)
                closed.Add(new ClosedPlayback { Entity = entity, Slot = i, Value = playbacks[i] });
            playbacks.Clear();
        }

        public static void SortRequests(RequestEntry* items, int count)
        {
            for (var i = 1; i < count; i++)
            {
                var item = items[i];
                var j = i - 1;
                while (j >= 0 && Before(in item, items + j))
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

        static bool Before(in RequestEntry left, in RequestEntry* right)
        {
            if (left.Entity != right->Entity)
                return left.Entity < right->Entity;
            if (left.Slot != right->Slot)
                return left.Slot < right->Slot;
            return left.Sequence < right->Sequence;
        }
    }
}
