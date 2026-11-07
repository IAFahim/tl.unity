using System;
using Tl.Unity;
using Unity.Entities;

namespace Tl
{
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TlBaking
    {
        public static Func<uint> TickRateResolver;

        public static TlTimeline GetTimeline(IBaker baker, UnityEngine.Object timeline)
        {
            if (timeline == null)
                return default;
            baker.DependsOn(timeline);
            return TlClipData.TimelineOf(timeline);
        }

        public static uint TicksPerSecond => TickRateResolver?.Invoke() ?? 60u;

        public static ushort Ticks(double seconds)
        {
            return seconds <= 0 ? (ushort)0 : (ushort)Math.Min(ushort.MaxValue, Math.Round(seconds * TicksPerSecond));
        }
    }
}
