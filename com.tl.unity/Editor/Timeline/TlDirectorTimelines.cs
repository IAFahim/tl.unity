using System.Collections.Generic;
using Unity.Entities;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlDirectorTimelines
    {
        public static List<TimelineAsset> Collect(IBaker baker, PlayableDirector director)
        {
            var result = new List<TimelineAsset>();
            var visited = new HashSet<TimelineAsset>();
            var queue = new Queue<TimelineAsset>();
            baker.DependsOn(director.playableAsset);
            if (director.playableAsset is TimelineAsset first)
                queue.Enqueue(first);
            while (queue.Count > 0)
            {
                var timeline = queue.Dequeue();
                if (timeline == null || !visited.Add(timeline))
                    continue;
                baker.DependsOn(timeline);
                if (timeline.markerTrack != null)
                    baker.DependsOn(timeline.markerTrack);
                var settings = Settings(timeline);
                if (settings != null)
                {
                    baker.DependsOn(settings);
                    if (settings.Next != null)
                        baker.DependsOn(settings.Next);
                    if (settings.End == TlEnd.Next && settings.Next != null)
                        queue.Enqueue(settings.Next);
                }
                var tracks = 0;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track is not TlTrack tl)
                        continue;
                    baker.DependsOn(tl);
                    for (var parent = tl.parent as TrackAsset; parent != null; parent = parent.parent as TrackAsset)
                        baker.DependsOn(parent);
                    foreach (var clip in tl.GetClips())
                        baker.DependsOn(clip.asset);
                    tracks++;
                }
                if (tracks > 0)
                    result.Add(timeline);
            }
            return result;
        }

        public static TlTimelineSettings Settings(TimelineAsset timeline)
        {
            if (timeline == null || timeline.markerTrack == null)
                return null;
            foreach (var marker in timeline.markerTrack.GetMarkers())
                if (marker is TlTimelineSettings settings)
                    return settings;
            return null;
        }
    }
}
