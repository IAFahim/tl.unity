using System;
using UnityEngine;

namespace Tl.Unity
{
    public enum KindSets
    {
        Exact,
        UnionPerProfile
    }

    [CreateAssetMenu(fileName = "TimelineLibrary", menuName = "Tl/Timeline Library")]
    public sealed class TimelineLibraryAsset : ScriptableObject
    {
        [Tooltip("Folder holding library.json, the timeline files and game.tlib. Project-relative, Packages/ paths work.")]
        public string Folder = string.Empty;

        [Tooltip("Generated class with one ushort constant per timeline, for example Game.Timelines.")]
        public string TimelineIds = string.Empty;

        [Tooltip("Script assembly that declares the kinds, feeders and pulses, for example Game.Logic.")]
        public string GameAssembly = string.Empty;

        [Tooltip("Namespace of the generated KindCapacity class.")]
        public string Namespace = "Game";

        [Tooltip("Where the bake writes Kinds.g.cs. It is rewritten only when a capacity changes. Leave empty to bake no capacities from this library.")]
        public string KindsFile = string.Empty;

        [Tooltip("The tlb command, or a path to tlb or Tl.Bake.dll.")]
        public string Tlb = "tlb";

        public KindSets KindSets = KindSets.Exact;
    }
}
