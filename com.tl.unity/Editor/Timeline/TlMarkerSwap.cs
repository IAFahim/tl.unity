using UnityEditor;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlMarkerSwap
    {
        public static TlMarker Replace(TlMenuMarker stub)
        {
            var track = stub.parent;
            if (track == null)
                return null;
            var group = Undo.GetCurrentGroup();
            var template = stub.CreateTemplate();
            var time = stub.time;
            track.DeleteMarker(stub);
            var marker = track.CreateMarker<TlMarker>(time);
            marker.Data = template;
            marker.name = TlMenu.ShortName(template.MenuName);
            EditorUtility.SetDirty(marker);
            Undo.SetCurrentGroupName("Add " + marker.name + " Marker");
            Undo.CollapseUndoOperations(group);
            return marker;
        }
    }
}
