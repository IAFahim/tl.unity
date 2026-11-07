using Tl;
using Unity.Entities;

[TlClip("Crowd/Hop")]
public struct HopClip
{
    public float Height;
    public TlCurve Shape;
}

[TlClip("Crowd/Spin")]
public struct SpinClip
{
    public float Turns;
    public TlCurve Ease;
}

[TlClip("Crowd/Glow")]
public struct GlowClip
{
    public float Strength;
    public TlCurve Wave;
}

public struct DancePose : IComponentData
{
    public float Lift;
    public float Yaw;
    public float Glow;
}

[TlKind]
public struct CrowdLift
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref DancePose pose) => pose.Lift += total;
}

[TlKind]
public struct CrowdYaw
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref DancePose pose) => pose.Yaw += total;
}

[TlKind]
public struct CrowdGlow
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref DancePose pose) => pose.Glow += total;
}

public struct HopTrack : ITlTrack<HopClip>
{
    [Feeds(typeof(CrowdLift))]
    public static float Evaluate(in TlFrame<HopClip> frame) => frame.Clip.Height * frame.Clip.Shape.Evaluate(frame.Progress);
}

public struct SpinTrack : ITlTrack<SpinClip>
{
    [Feeds(typeof(CrowdYaw))]
    public static float Evaluate(in TlFrame<SpinClip> frame) => frame.Clip.Turns * 360f * frame.Clip.Ease.Evaluate(frame.Progress);
}

public struct GlowTrack : ITlTrack<GlowClip>
{
    [Feeds(typeof(CrowdGlow))]
    public static float Evaluate(in TlFrame<GlowClip> frame) => frame.Clip.Strength * frame.Clip.Wave.Evaluate(frame.Progress);
}
