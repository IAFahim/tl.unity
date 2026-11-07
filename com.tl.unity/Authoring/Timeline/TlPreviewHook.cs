using System;
using UnityEngine.Playables;

namespace Tl.Unity
{
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TlPreviewHook
    {
        public static Action<PlayableDirector> Evaluate;
    }

    public sealed class TlPreviewMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            if (UnityEngine.Application.isPlaying || TlPreviewHook.Evaluate == null)
                return;
            if (playable.GetGraph().GetResolver() is PlayableDirector director)
                TlPreviewHook.Evaluate(director);
        }
    }
}

namespace Tl.Unity
{
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TlPreviewProperties
    {
        public static readonly string[] Transform =
        {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
            "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w",
            "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z",
        };
    }
}
