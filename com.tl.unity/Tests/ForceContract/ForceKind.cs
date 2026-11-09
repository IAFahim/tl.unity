using Tl;
using Unity.Mathematics;

[TlKind]
public struct ForceKind
{
    public static void Combine(ref float3 total, in float3 next) => total += next;
    public static void Apply(in float3 total, ref Force force) => force.Value += total;
}

public struct ForceTrack : ITlTrack<ForceClip>
{
    [Feeds(typeof(ForceKind))]
    public static float3 Evaluate(in TlFrame<ForceClip> frame)
        => frame.Clip.Direction * frame.Clip.Strength * frame.Clip.Falloff.Evaluate(frame.Progress) * frame.Direction;
}
