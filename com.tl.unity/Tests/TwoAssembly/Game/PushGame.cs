using PushPack;
using Tl;

namespace PushGame
{
    [TlState]
    public partial struct Wind
    {
        public float Scale;
    }

    [TlClip("Gust")]
    public struct GustClip
    {
        public float Speed;
        public float Lift;
    }

    public struct GustTrack : ITlTrack<GustClip>
    {
        [Feeds(typeof(PushKind))]
        public static PushValue Evaluate(in TlFrame<GustClip> frame, in Wind wind)
            => new PushValue { X = frame.Clip.Speed * wind.Scale * frame.Direction, Y = frame.Clip.Lift * frame.Progress };
    }
}
