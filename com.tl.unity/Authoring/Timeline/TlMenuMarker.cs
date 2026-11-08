using UnityEngine.Timeline;

namespace Tl.Unity
{
    public abstract class TlMenuMarker : Marker
    {
        public abstract TlMarkerData CreateTemplate();
    }
}
