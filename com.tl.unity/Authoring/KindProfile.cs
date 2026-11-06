using System;
using UnityEngine;

namespace Tl.Unity
{
    [CreateAssetMenu(fileName = "KindProfile", menuName = "Tl/Kind Profile")]
    public sealed class KindProfile : ScriptableObject
    {
        [Tooltip("Kinds every entity in this profile gets, by name, so the profile shares one archetype.")]
        public string[] Kinds = Array.Empty<string>();
    }
}
