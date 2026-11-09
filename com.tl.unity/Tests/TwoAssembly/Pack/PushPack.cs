using Tl;

namespace PushPack
{
    [TlColumn]
    public partial struct Push
    {
        public float X;
        public float Y;
    }

    [TlClip("Push")]
    public struct PushClip
    {
        public float X;
        public float Y;
        public float Strength;
    }

    [TlKind]
    public struct PushKind
    {
        public static void Combine(ref PushValue total, in PushValue next)
        {
            total.X += next.X;
            total.Y += next.Y;
        }

        public static void Apply(in PushValue total, ref Push push)
        {
            push.X += total.X;
            push.Y += total.Y;
        }
    }

    public struct PushValue
    {
        public float X;
        public float Y;
    }

    public struct PushTrack : ITlTrack<PushClip>
    {
        [Feeds(typeof(PushKind))]
        public static PushValue Evaluate(in TlFrame<PushClip> frame)
        {
            var scale = frame.Clip.Strength * (1f - frame.Progress) * frame.Direction;
            return new PushValue { X = frame.Clip.X * scale, Y = frame.Clip.Y * scale };
        }
    }
}
