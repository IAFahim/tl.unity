using ParityPack;
using Tl;

namespace ParityGame
{
    [TlState]
    public partial struct Stage
    {
        public int Offset;
    }

    [TlClip("Parity/Line")]
    public struct LineClip
    {
        public int Line;
    }

    [TlClip("Parity/Reel")]
    public struct ReelClip
    {
        public int Reel;
    }

    public struct LineTrack : ITlTrack<LineClip>
    {
        [Feeds(typeof(Caption))]
        public static int Which(in TlFrame<LineClip> frame, in Stage stage) => frame.Clip.Line + stage.Offset;
    }

    public struct ReelTrack : ITlTrack<ReelClip>
    {
        [Feeds(typeof(Reel))]
        public static int Which(in TlFrame<ReelClip> frame, in Stage stage) => frame.Clip.Reel + stage.Offset;
    }
}
