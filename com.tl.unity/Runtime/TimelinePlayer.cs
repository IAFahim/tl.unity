using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    public static unsafe class TimelinePlayer
    {
        public static bool Start(ref DynamicBuffer<Playback> playbacks, int entity, global::Tl.TimelineId id, ushort at, in global::Tl.LibraryView view, ref NativeList<ClosedPlayback> closed)
        {
            return TimelineKernel.ApplyStart(ref playbacks, entity, id.Value, at, in view, ref closed, out _);
        }

        public static bool Stop(ref DynamicBuffer<Playback> playbacks, int entity, global::Tl.TimelineId id, ref NativeList<ClosedPlayback> closed)
        {
            return TimelineKernel.ApplyStop(ref playbacks, entity, id.Value, ref closed);
        }

        public static bool StopChannel(ref DynamicBuffer<Playback> playbacks, int entity, byte channel, ref NativeList<ClosedPlayback> closed)
        {
            return TimelineKernel.ApplyStopChannel(ref playbacks, entity, channel, ref closed);
        }

        public static void StopAll(ref DynamicBuffer<Playback> playbacks, int entity, ref NativeList<ClosedPlayback> closed)
        {
            TimelineKernel.ApplyStopAll(ref playbacks, entity, ref closed);
        }

        public static bool IsPlaying(ref DynamicBuffer<Playback> playbacks, global::Tl.TimelineId id)
        {
            for (var i = 0; i < playbacks.Length; i++)
                if (playbacks[i].Timeline == id.Value)
                    return true;
            return false;
        }

        public static bool Find(ref DynamicBuffer<Playback> playbacks, byte channel, out Playback playback)
        {
            for (var i = 0; i < playbacks.Length; i++)
            {
                if (playbacks[i].Channel == channel && channel != 0)
                {
                    playback = playbacks[i];
                    return true;
                }
            }
            playback = default;
            return false;
        }
    }

    public static class TimelineSlots
    {
        public static bool TryGetTimeline(ref DynamicBuffer<TimelineSlot> slots, ushort key, out global::Tl.TimelineId timeline)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Key == key)
                {
                    timeline = slots[i].Timeline;
                    return true;
                }
            }
            timeline = default;
            return false;
        }

        public static void Set(ref DynamicBuffer<TimelineSlot> slots, ushort key, global::Tl.TimelineId timeline)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Key == key)
                {
                    slots[i] = new TimelineSlot { Key = key, Timeline = timeline };
                    return;
                }
            }
            slots.Add(new TimelineSlot { Key = key, Timeline = timeline });
        }

        public static unsafe bool StartFromSlot(ref DynamicBuffer<Playback> playbacks, ref DynamicBuffer<TimelineSlot> slots, int entity, ushort key, ushort at, in global::Tl.LibraryView view, ref NativeList<ClosedPlayback> closed)
        {
            if (!TryGetTimeline(ref slots, key, out var timeline) || timeline.IsNone)
                return false;
            return TimelinePlayer.Start(ref playbacks, entity, timeline, at, in view, ref closed);
        }
    }
}
