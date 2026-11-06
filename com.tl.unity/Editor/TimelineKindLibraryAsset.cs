using System;
using UnityEngine;

namespace Tl.Unity.Editor
{
    [CreateAssetMenu(fileName = "TimelineKindLibrary", menuName = "Tl/Timeline Kind Library")]
    public sealed class TimelineKindLibraryAsset : ScriptableObject
    {
        public KindSets KindSets = KindSets.Exact;
        public KindProfile[] Profiles = Array.Empty<KindProfile>();
        public string LibraryJsonPath = string.Empty;
        public string KindsGcsPath = string.Empty;
        public string GameNamespace = "Game";

        public bool TryGetProfile(string name, out KindProfile profile)
        {
            profile = null;
            if (string.IsNullOrEmpty(name) || Profiles == null)
                return false;
            for (var i = 0; i < Profiles.Length; i++)
                if (Profiles[i] != null && Profiles[i].ProfileName == name)
                {
                    profile = Profiles[i];
                    return true;
                }
            return false;
        }
    }
}
