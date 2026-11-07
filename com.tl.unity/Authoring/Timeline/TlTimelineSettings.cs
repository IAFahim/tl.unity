using System.ComponentModel;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [DisplayName("Tl Settings")]
    public sealed class TlTimelineSettings : Marker
    {
        [Tooltip("What happens when this timeline reaches its end.")]
        public TlEnd End;

        [Tooltip("The timeline that starts when this one ends. Used when End is Next.")]
        public TimelineAsset Next;

        [Tooltip("Only one timeline plays per channel: starting another one on the same channel replaces it. 0 means no channel.")]
        [Range(0, 15)]
        public int Channel;
    }
}
