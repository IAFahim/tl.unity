using System;
using UnityEngine;

namespace Tl.Unity
{
    [DisallowMultipleComponent]
    public sealed class TimelinePlayerAuthoring : MonoBehaviour
    {
        [Tooltip("The library whose timelines this player runs.")]
        public TimelineLibraryAsset Library;

        [Tooltip("Timelines started when the entity spawns, by name.")]
        public string[] StartWith = Array.Empty<string>();

        [Tooltip("Other timelines game code may start on this entity, by name. Their kinds and their next chains are added too.")]
        public string[] MayStart = Array.Empty<string>();

        [Tooltip("Kinds game code sends pulses to on this entity, by name.")]
        public string[] Kinds = Array.Empty<string>();

        [Tooltip("With Kind Sets set to Union Per Profile on the library, the entity also gets every kind of this profile.")]
        public KindProfile Profile;
    }
}
