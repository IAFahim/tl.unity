using UnityEditor;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlTrackSwap
    {
        public static TlTrack Replace(TlMenuTrack stub)
        {
            var timeline = stub.timelineAsset;
            if (timeline == null)
                return null;
            var group = Undo.GetCurrentGroup();
            var parent = stub.parent as TrackAsset;
            var template = stub.CreateTemplate();
            timeline.DeleteTrack(stub);
            var track = timeline.CreateTrack<TlTrack>(parent, TlMenu.ShortName(template.MenuName));
            track.Template = template;
            EditorUtility.SetDirty(track);
            Undo.SetCurrentGroupName("Add " + track.name + " Track");
            Undo.CollapseUndoOperations(group);
            return track;
        }
    }
}
