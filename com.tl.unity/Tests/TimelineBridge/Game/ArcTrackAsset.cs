using UnityEngine.Timeline;

namespace KindGame.Bridge
{
    [TrackClipType(typeof(ArcClipAsset))]
    public sealed class ArcTrackAsset : Tl.Unity.TlTrack<ArcTrack>
    {
    }
}
