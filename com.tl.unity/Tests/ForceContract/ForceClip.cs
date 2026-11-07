using Tl;
using Unity.Mathematics;

[TlClip("Force")]
public struct ForceClip
{
    public float3 Direction;
    public float Strength;
    public TlCurve Falloff;
}
