using Tl.Generated;

namespace Tl.Unity.Tracks.Tests
{
    public sealed class TestForceMenu : TlMenuTrack
    {
        public override TlClipData CreateTemplate() => new ForceClip_TlData();
    }
}
