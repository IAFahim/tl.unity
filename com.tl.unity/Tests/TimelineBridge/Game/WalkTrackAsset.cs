using UnityEngine.Timeline;

namespace KindGame.Bridge
{
    [TrackClipType(typeof(WalkClipAsset))]
    public sealed class WalkTrackAsset : Tl.Unity.TlTrack<WalkTrack>
    {
    }
}
