using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlDirectorTimelines
    {
        public static List<TimelineAsset> Collect(IBaker baker, PlayableDirector director, TlAlsoPlays alsoPlays = null)
        {
            var result = new List<TimelineAsset>();
            var visited = new HashSet<TimelineAsset>();
            var queue = new Queue<TimelineAsset>();
            baker.DependsOn(director.playableAsset);
            if (director.playableAsset is TimelineAsset first)
                queue.Enqueue(first);
            if (alsoPlays != null)
                foreach (var other in alsoPlays.Timelines)
                    if (other != null)
                    {
                        baker.DependsOn(other);
                        queue.Enqueue(other);
                    }
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
                    {
                        baker.DependsOn(clip.asset);
                        foreach (var target in Targets((clip.asset as TlClip)?.Data))
                        {
                            baker.DependsOn(target);
                            queue.Enqueue(target);
                        }
                    }
                    tracks++;
                }
                if (tracks > 0)
                    result.Add(timeline);
            }
            return result;
        }

        public static IEnumerable<TimelineAsset> Targets(TlClipData data)
        {
            if (data == null)
                yield break;
            foreach (var field in data.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (field.FieldType == typeof(TimelineAsset) && field.GetValue(data) is TimelineAsset target && target != null)
                    yield return target;
        }

        public static IEnumerable<TimelineAsset> IdFields(TlClipData data)
        {
            if (data == null)
                yield break;
            foreach (var field in data.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType != typeof(TimelineAsset) || !(field.GetValue(data) is TimelineAsset target) || target == null)
                    continue;
                var clipField = data.ClipType.GetField(field.Name, BindingFlags.Instance | BindingFlags.Public);
                if (clipField != null && clipField.FieldType == typeof(global::Tl.TimelineId))
                    yield return target;
            }
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
