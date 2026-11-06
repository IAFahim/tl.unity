using UnityEngine.Timeline;

namespace KindGame.Bridge
{
    [TrackClipType(typeof(StatClipAsset))]
    public sealed class StatTrackAsset : Tl.Unity.TlTrack<StatTrack>
    {
    }
}
