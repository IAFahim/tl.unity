using UnityEngine.Timeline;

namespace KindGame.Bridge
{
    [TrackClipType(typeof(HitClipAsset))]
    public sealed class HitTrackAsset : Tl.Unity.TlTrack<HitTrack>
    {
    }
}
