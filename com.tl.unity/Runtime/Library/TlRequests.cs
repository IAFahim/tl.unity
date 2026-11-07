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
    }
}
