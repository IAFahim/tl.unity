using System;

namespace Tl.Unity
{
    [Serializable]
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public abstract class TlClipData
    {
        public static Func<UnityEngine.Object, global::Unity.Entities.Hash128> AssetResolver;
        public static Func<UnityEngine.Object, ushort> LocalIdResolver;

        public abstract string MenuName { get; }
        public abstract Type ClipType { get; }
        public abstract int ClipSize { get; }
        public abstract TlClipData CreateDefault();
        public abstract void WriteClip(byte[] destination, int offset);
        public abstract void Bake(ref TlBaker baker);

        public static TlTimeline TimelineOf(UnityEngine.Object timeline)
        {
            return timeline == null || AssetResolver == null ? default : new TlTimeline { Asset = AssetResolver(timeline).ToAssetId() };
        }

        public static global::Tl.TimelineId IdOf(UnityEngine.Object timeline)
        {
            return new global::Tl.TimelineId(timeline == null || LocalIdResolver == null ? (ushort)0 : LocalIdResolver(timeline));
        }
    }
}
