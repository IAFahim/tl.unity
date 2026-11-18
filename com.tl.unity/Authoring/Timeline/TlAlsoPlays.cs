using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayableDirector))]
    [AddComponentMenu("")]
    public sealed class TlAlsoPlays : MonoBehaviour
    {
        [Tooltip("Timelines this object can play besides its Playable, for example when code or another timeline starts them.")]
        public List<TimelineAsset> Timelines = new List<TimelineAsset>();

        [Tooltip("Timelines from Also Plays that start by themselves when the scene loads, at tick 0, after the Playable Director's own Play On Awake.")]
        public List<TimelineAsset> PlayOnAwake = new List<TimelineAsset>();
    }
}
