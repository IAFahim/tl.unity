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

        [Tooltip("Timelines that start by themselves when the scene loads: after the Playable, at tick 0, in the order of the Also Plays list. The Playable listed here starts at the director's Initial Time, as with the director's own Play On Awake.")]
        public List<TimelineAsset> PlayOnAwake = new List<TimelineAsset>();
    }
}
