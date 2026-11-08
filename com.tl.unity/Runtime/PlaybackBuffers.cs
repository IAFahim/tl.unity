using Unity.Entities;

namespace Tl.Unity
{
    [InternalBufferCapacity(4)]
    public struct Playback : IBufferElementData
    {
        public ushort Timeline;
        public ushort Tick;
        public byte Channel;
        public global::Tl.PlaybackFlags Flags;
        public ushort Reserved;
    }

    [InternalBufferCapacity(4)]
    public struct TimelineSlot : IBufferElementData
    {
        public ushort Key;
        public global::Tl.TimelineId Timeline;
    }

    [InternalBufferCapacity(4)]
    public struct TimelineRequest : IBufferElementData
    {
        public int Slot;
        public ushort Timeline;
        public ushort At;
        public global::Tl.RequestOp Op;
        public Hash128 Asset;

        public static TimelineRequest Start(int slot, ushort timeline, ushort at = 0)
        {
            return new TimelineRequest { Slot = slot, Timeline = timeline, At = at, Op = global::Tl.RequestOp.Start };
        }

        public static TimelineRequest Stop(int slot, ushort timeline)
        {
            return new TimelineRequest { Slot = slot, Timeline = timeline, Op = global::Tl.RequestOp.Stop };
        }

        public static TimelineRequest StopChannel(int slot, byte channel)
        {
            return new TimelineRequest { Slot = slot, Timeline = channel, Op = global::Tl.RequestOp.StopChannel };
        }

        public static TimelineRequest StopAll(int slot)
        {
            return new TimelineRequest { Slot = slot, Op = global::Tl.RequestOp.StopAll };
        }

        public static TimelineRequest Pause(int slot, ushort timeline)
        {
            return new TimelineRequest { Slot = slot, Timeline = timeline, Op = global::Tl.RequestOp.Pause };
        }

        public static TimelineRequest Resume(int slot, ushort timeline)
        {
            return new TimelineRequest { Slot = slot, Timeline = timeline, Op = global::Tl.RequestOp.Resume };
        }

        public static TimelineRequest Seek(int slot, ushort timeline, ushort tick)
        {
            return new TimelineRequest { Slot = slot, Timeline = timeline, At = tick, Op = global::Tl.RequestOp.Seek };
        }

        public static TimelineRequest Reverse(int slot, ushort timeline, bool reverse)
        {
            return new TimelineRequest { Slot = slot, Timeline = timeline, At = reverse ? (ushort)1 : (ushort)0, Op = global::Tl.RequestOp.Reverse };
        }

        public static TimelineRequest PauseChannel(int slot, byte channel)
        {
            return new TimelineRequest { Slot = slot, Timeline = channel, Op = global::Tl.RequestOp.PauseChannel };
        }

        public static TimelineRequest ResumeChannel(int slot, byte channel)
        {
            return new TimelineRequest { Slot = slot, Timeline = channel, Op = global::Tl.RequestOp.ResumeChannel };
        }

        public static TimelineRequest SeekChannel(int slot, byte channel, ushort tick)
        {
            return new TimelineRequest { Slot = slot, Timeline = channel, At = tick, Op = global::Tl.RequestOp.SeekChannel };
        }

        public static TimelineRequest ReverseChannel(int slot, byte channel, bool reverse)
        {
            return new TimelineRequest { Slot = slot, Timeline = channel, At = reverse ? (ushort)1 : (ushort)0, Op = global::Tl.RequestOp.ReverseChannel };
        }
    }

    [InternalBufferCapacity(8)]
    public struct ClosedPlayback : IBufferElementData
    {
        public int Entity;
        public int Slot;
        public Playback Value;
    }
}
