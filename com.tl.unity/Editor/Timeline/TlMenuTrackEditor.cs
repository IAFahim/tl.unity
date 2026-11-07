using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    [CustomTimelineEditor(typeof(TlMenuTrack))]
    public sealed class TlMenuTrackEditor : TrackEditor
    {
        public override void OnCreate(TrackAsset track, TrackAsset copiedFrom)
        {
            if (track is TlMenuTrack stub)
                TlTrackSwap.Replace(stub);
        }
    }
}
