using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    public enum TimelineEnd
    {
        Remove,
        Loop,
        Hold,
        Next
    }

    public enum BridgeOutput
    {
        JsonInLibraryFolder,
        StagedJson
    }

    [Serializable]
    public sealed class BridgedTimeline
    {
        [Tooltip("The Unity Timeline asset to bake.")]
        public TimelineAsset Timeline;

        [Tooltip("Tl timeline name and json file name. Empty uses the Timeline asset name.")]
        public string Name = string.Empty;

        [Tooltip("What happens at the end: removed, loop, hold the last tick, or start Next.")]
        public TimelineEnd End = TimelineEnd.Remove;

        [Tooltip("The timeline started when this one ends, when End is Next. It must be in this bridge too.")]
        public TimelineAsset Next;

        [Tooltip("Exclusivity channel name from library.json. Empty stacks freely.")]
        public string Channel = string.Empty;
    }

    [CreateAssetMenu(fileName = "TimelineBridge", menuName = "Tl/Timeline Bridge")]
    public sealed class TimelineBridgeAsset : ScriptableObject
    {
        [Tooltip("The Timeline Library whose folder receives the baked timelines and game.tlib.")]
        public TimelineLibraryAsset Library;

        [Tooltip("Json In Library Folder writes one json per timeline into the library folder. Staged Json writes them to Temp and only game.tlib reaches the project.")]
        public BridgeOutput Output = BridgeOutput.JsonInLibraryFolder;

        [Tooltip("Project path where tlb writes the timeline ids class. Empty keeps the tlb default.")]
        public string IdsFile = string.Empty;

        [Tooltip("Bake again when a listed Timeline asset is saved.")]
        public bool BakeOnSave = true;

        public List<BridgedTimeline> Timelines = new List<BridgedTimeline>();

        public BridgedTimeline Find(TimelineAsset timeline)
        {
            if (timeline != null)
                foreach (var entry in Timelines)
                    if (entry != null && entry.Timeline == timeline)
                        return entry;
            return null;
        }

        public string NameOf(TimelineAsset timeline)
        {
            var entry = Find(timeline);
            if (entry == null)
                return null;
            return string.IsNullOrEmpty(entry.Name) ? timeline.name : entry.Name;
        }

        public string ConstantOf(TimelineAsset timeline)
        {
            var name = NameOf(timeline);
            return name == null ? null : ConstantName(name);
        }

        public static string ConstantName(string timelineName)
        {
            var text = new StringBuilder();
            foreach (var part in (timelineName ?? string.Empty).Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries))
                text.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            if (text.Length > 0 && char.IsDigit(text[0]))
                text.Insert(0, '_');
            return text.ToString();
        }
    }
}
