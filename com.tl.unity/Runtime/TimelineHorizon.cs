using Unity.Entities;

namespace Tl.Unity
{
    public struct TimelineHorizon : IComponentData
    {
        public byte Steps;
    }

    public struct TimelineGlide : IComponentData
    {
        public byte Limit;

        public static TimelineGlide Default => new TimelineGlide { Limit = global::Tl.Horizon.Max };
    }
}
