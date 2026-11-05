using System;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public enum KindSets
    {
        Exact,
        UnionPerProfile
    }

    [CreateAssetMenu(fileName = "KindProfile", menuName = "Tl/Kind Profile")]
    public sealed class KindProfile : ScriptableObject
    {
        public string ProfileName = string.Empty;
        public string[] Kinds = Array.Empty<string>();

        public bool Declares(string kind)
        {
            if (string.IsNullOrEmpty(kind) || Kinds == null)
                return false;
            for (var i = 0; i < Kinds.Length; i++)
                if (Kinds[i] == kind)
                    return true;
            return false;
        }
    }

// SCAFFOLD (tl#557)
    public static class KindProfileSets
    {
        public static int[] ResolveUnion(KindProfile profile, KindRegistry registry, KindSets mode)
        {
            var kinds = new System.Collections.Generic.List<int>();
            if (mode == KindSets.Exact || profile == null || registry == null)
                return kinds.ToArray();
            for (var i = 0; i < registry.Count; i++)
                if (profile.Declares(registry[i].Name))
                    kinds.Add(i);
            return kinds.ToArray();
        }
    }
}
