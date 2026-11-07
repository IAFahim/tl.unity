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
    }
}
