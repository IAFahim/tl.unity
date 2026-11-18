using Tl;

namespace ManyAtOnce
{
    [TlClip("Many At Once/Height")]
    public struct HeightClip : IBlend<HeightClip>
    {
        public float Height;

        public void Blend(in HeightClip first, in HeightClip second, float factor, out HeightClip result)
            => result = new HeightClip { Height = first.Height + (second.Height - first.Height) * factor };
    }

    public struct HeightTrack : ITlTrack<HeightClip>
    {
        [Feeds(typeof(HeightKind))]
        public static float HeightClipToHeightThisTick(in TlFrame<HeightClip> frame)
            => frame.Clip.Height;
    }

    [TlKind]
    public struct HeightKind
    {
        public static void Combine(ref float heightFromClipsSoFar, in float heightFromAnotherClip)
            => heightFromClipsSoFar += heightFromAnotherClip;

        public static void Apply(in float totalHeightThisTick, ref Platform platform)
            => platform.Height = totalHeightThisTick;
    }

    [TlState]
    public partial struct Platform
    {
        public float Height;
    }
}
