using Tl;

namespace Ui;

public struct SlideTrack : IBlend<SlideClip>
{
    public float Scale;
    public void Blend(in SlideClip first, in SlideClip second, float factor, out SlideClip result)
        => result = new() { Pixels = first.Pixels + (second.Pixels - first.Pixels) * factor };
}

public struct SlideClip { public float Pixels; }

public readonly struct Slide : ITrack<SlideTrack, SlideClip>
{
    public static void Fold(in Frame<SlideTrack, SlideClip> frame, out float pixels)
        => pixels = frame.Direction * frame.Clip.Pixels * frame.Track.Scale;
}
