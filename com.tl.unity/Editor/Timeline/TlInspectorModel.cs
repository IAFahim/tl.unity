using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlInspectorModel
    {
        public const string NoTimeline = "This object has no timeline. Drag one into Playable.";
        public const string NotInSubScene = "This object is not in a SubScene, so Tl will not run it.";
        public const string NoTlTracks = "No Tl tracks yet. In the Timeline window click + and pick a Tl track.";

        public static int TlTracks(TimelineAsset timeline) => timeline == null ? 0 : timeline.GetOutputTracks().OfType<TlTrack>().Count();

        public static string Status(TimelineAsset timeline)
        {
            if (timeline == null)
                return "";
            var tracks = timeline.GetOutputTracks().OfType<TlTrack>().ToList();
            if (tracks.Count == 0)
                return NoTlTracks;
            var clips = tracks.Sum(t => t.GetClips().Count());
            var settings = TlDirectorTimelines.Settings(timeline);
            if (settings != null && settings.End == TlEnd.Next && settings.Next == timeline && clips == 0)
                return "Play next is this same timeline and it is empty. It would restart forever.";
            return "OK. " + Count(tracks.Count, "Tl track") + ", " + Count(clips, "clip") + ".";
        }

        public static string FrameRateHint(TimelineAsset timeline, uint ticksPerSecond)
        {
            if (timeline == null)
                return null;
            var rate = timeline.editorSettings.frameRate;
            if (System.Math.Abs(rate - ticksPerSecond) < 1e-6)
                return null;
            return "This timeline snaps to " + rate.ToString("0.##", CultureInfo.InvariantCulture) + " frames per second, but the game runs " + ticksPerSecond + " ticks per second. Clips are rounded to ticks when they bake.";
        }

        public static List<string> DirectorMessages(PlayableDirector director, TlAlsoPlays alsoPlays)
        {
            var messages = new List<string>();
            var timelines = Plays(director, alsoPlays);
            if (director.playableAsset == null && timelines.Count == 0)
            {
                messages.Add(NoTimeline);
                return messages;
            }
            if (timelines.Any(t => TlTracks(t) > 0) && !director.gameObject.scene.isSubScene)
                messages.Add(NotInSubScene);
            if (director.playableAsset is TimelineAsset first && TlTracks(first) == 0)
                messages.Add(first.name + ": " + NoTlTracks);
            return messages;
        }

        public static List<TimelineAsset> Plays(PlayableDirector director, TlAlsoPlays alsoPlays)
        {
            var roots = new List<TimelineAsset>();
            if (director.playableAsset is TimelineAsset first)
                roots.Add(first);
            if (alsoPlays != null)
                roots.AddRange(alsoPlays.Timelines);
            return Closure(roots);
        }

        public static List<TimelineAsset> Closure(IEnumerable<TimelineAsset> roots)
        {
            var result = new List<TimelineAsset>();
            var queue = new Queue<TimelineAsset>(roots);
            while (queue.Count > 0)
            {
                var timeline = queue.Dequeue();
                if (timeline == null || result.Contains(timeline))
                    continue;
                result.Add(timeline);
                var settings = TlDirectorTimelines.Settings(timeline);
                if (settings != null && settings.End == TlEnd.Next && settings.Next != null)
                    queue.Enqueue(settings.Next);
                foreach (var track in timeline.GetOutputTracks().OfType<TlTrack>())
                    foreach (var clip in track.GetClips())
                        foreach (var target in TlDirectorTimelines.Targets((clip.asset as TlClip)?.Data))
                            queue.Enqueue(target);
            }
            return result;
        }

        public static void SetSettings(TimelineAsset timeline, TlEnd end, TimelineAsset next, int channel)
        {
            var settings = TlDirectorTimelines.Settings(timeline);
            if (settings != null && settings.End == end && settings.Next == next && settings.Channel == channel)
                return;
            if (settings == null)
            {
                if (end == TlEnd.Stop && next == null && channel == 0)
                    return;
                Undo.RegisterCompleteObjectUndo(timeline, "Tl Settings");
                if (timeline.markerTrack == null)
                    timeline.CreateMarkerTrack();
                Undo.RegisterCompleteObjectUndo(timeline.markerTrack, "Tl Settings");
                settings = timeline.markerTrack.CreateMarker<TlTimelineSettings>(0);
                Undo.RegisterCreatedObjectUndo(settings, "Tl Settings");
                if (AssetDatabase.Contains(timeline) && !AssetDatabase.Contains(settings))
                    AssetDatabase.AddObjectToAsset(settings, timeline);
            }
            Undo.RecordObject(settings, "Tl Settings");
            settings.End = end;
            settings.Next = next;
            settings.Channel = UnityEngine.Mathf.Clamp(channel, 0, 15);
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(timeline);
        }

        public static TlAlsoPlays AlsoPlays(PlayableDirector director, bool create)
        {
            var also = director.GetComponent<TlAlsoPlays>();
            if (also == null && create)
            {
                also = Undo.AddComponent<TlAlsoPlays>(director.gameObject);
                also.hideFlags |= UnityEngine.HideFlags.HideInInspector;
            }
            return also;
        }

        static string Count(int n, string what) => n + " " + what + (n == 1 ? "" : "s");
    }
}
