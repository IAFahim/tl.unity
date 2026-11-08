using System.ComponentModel;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [HideInMenu]
    [DisplayName("Tl Marker")]
    public sealed class TlMarker : Marker
    {
        [SerializeReference] public TlMarkerData Data;
    }
}
