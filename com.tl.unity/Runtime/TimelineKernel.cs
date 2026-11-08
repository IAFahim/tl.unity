namespace Tl.Unity
{
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
    }
}
