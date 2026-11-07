using UnityEngine.Timeline;

namespace Tl.Unity
{
    public abstract class TlMenuTrack : TrackAsset
    {
        public abstract TlClipData CreateTemplate();
    }
}
