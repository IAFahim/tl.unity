using Tl.Unity;
using Unity.Entities;

namespace Tl
{
    public static class TlRequests
    {
        public static void Start(this DynamicBuffer<TimelineRequest> requests, in TlTimeline timeline, ushort atTick = 0)
        {
            requests.Add(new TimelineRequest { Slot = -1, At = atTick, Op = RequestOp.Start, Asset = timeline.Asset.ToHash128() });
        }

        public static void Stop(this DynamicBuffer<TimelineRequest> requests, in TlTimeline timeline)
        {
            requests.Add(new TimelineRequest { Slot = -1, Op = RequestOp.Stop, Asset = timeline.Asset.ToHash128() });
        }

        public static void Pause(this DynamicBuffer<TimelineRequest> requests, in TlTimeline timeline)
        {
            requests.Add(new TimelineRequest { Slot = -1, Op = RequestOp.Pause, Asset = timeline.Asset.ToHash128() });
        }

        public static void Resume(this DynamicBuffer<TimelineRequest> requests, in TlTimeline timeline)
        {
            requests.Add(new TimelineRequest { Slot = -1, Op = RequestOp.Resume, Asset = timeline.Asset.ToHash128() });
        }

        public static void Seek(this DynamicBuffer<TimelineRequest> requests, in TlTimeline timeline, ushort tick)
        {
            requests.Add(new TimelineRequest { Slot = -1, At = tick, Op = RequestOp.Seek, Asset = timeline.Asset.ToHash128() });
        }

        public static void Reverse(this DynamicBuffer<TimelineRequest> requests, in TlTimeline timeline, bool reverse)
        {
            requests.Add(new TimelineRequest { Slot = -1, At = reverse ? (ushort)1 : (ushort)0, Op = RequestOp.Reverse, Asset = timeline.Asset.ToHash128() });
        }

        public static void StopChannel(this DynamicBuffer<TimelineRequest> requests, byte channel)
        {
            requests.Add(TimelineRequest.StopChannel(-1, channel));
        }

        public static void StopAll(this DynamicBuffer<TimelineRequest> requests)
        {
            requests.Add(TimelineRequest.StopAll(-1));
        }

        public static void PauseChannel(this DynamicBuffer<TimelineRequest> requests, byte channel)
        {
            requests.Add(TimelineRequest.PauseChannel(-1, channel));
        }

        public static void ResumeChannel(this DynamicBuffer<TimelineRequest> requests, byte channel)
        {
            requests.Add(TimelineRequest.ResumeChannel(-1, channel));
        }

        public static void SeekChannel(this DynamicBuffer<TimelineRequest> requests, byte channel, ushort tick)
        {
            requests.Add(TimelineRequest.SeekChannel(-1, channel, tick));
        }

        public static void ReverseChannel(this DynamicBuffer<TimelineRequest> requests, byte channel, bool reverse)
        {
            requests.Add(TimelineRequest.ReverseChannel(-1, channel, reverse));
        }
    }
}
