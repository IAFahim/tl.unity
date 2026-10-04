using Tl;

namespace Rifle;

public struct DiagClip
{
    public bool Gate;
    public sbyte Step;
    public short Level;
    public int Phase;
    public long Span;
    public float Curve;
}

public struct DiagTrack : IBlend<DiagClip>
{
    public byte Scale;
    public void Blend(in DiagClip first, in DiagClip second, float factor, out DiagClip result) => result = new()
    {
        Gate = factor < .5f ? first.Gate : second.Gate,
        Step = first.Step, Level = first.Level, Phase = first.Phase, Span = first.Span,
        Curve = first.Curve + (second.Curve - first.Curve) * factor
    };
}

public readonly struct Diagnostics : ITrack<DiagTrack, DiagClip>
{
    public static void Fold(in Frame<DiagTrack, DiagClip> frame, out bool gate, out byte step, out short level, out int phase, out long span, out float curve)
    {
        gate = frame.Clip.Gate;
        step = (byte)(frame.Track.Scale * frame.Clip.Step);
        level = frame.Clip.Level;
        phase = frame.Clip.Phase * frame.Direction;
        span = frame.Clip.Span;
        curve = frame.Clip.Curve switch
        {
            -3f => float.NaN, -2f => float.PositiveInfinity, -1f => float.NegativeInfinity, 0f => -0f,
            _ => frame.Clip.Curve
        };
    }
}

public readonly struct DiagMirror : ITrack<DiagTrack, DiagClip>
{
    public static void Fold(in Frame<DiagTrack, DiagClip> frame, out bool gate, out long span)
    {
        gate = !frame.Clip.Gate;
        span = ~frame.Clip.Span;
    }
}
