using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    static class TlTimelineMenu
    {
        [MenuItem("Assets/Create/Tl/Timeline", priority = 450)]
        static void CreateTimeline()
        {
            ProjectWindowUtil.CreateAsset(TlTimelines.CreateAsset(), "New Timeline.playable");
        }
    }

    public static class TlTimelines
    {
        public static TimelineAsset CreateAsset()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.editorSettings.frameRate = TlProjectSettings.instance.TicksPerSecond;
            return timeline;
        }
    }
}
