using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    [CustomTimelineEditor(typeof(TlMenuMarker))]
    public sealed class TlMenuMarkerEditor : MarkerEditor
    {
        public override void OnCreate(IMarker marker, IMarker clonedFrom)
        {
            if (marker is TlMenuMarker stub)
                TlMarkerSwap.Replace(stub);
        }
    }
}
