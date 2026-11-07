using Tl;
using Tl.Mathematics;

namespace Tl.Spike
{
    [TlClip("Spike Force")]
    public struct SpikeForceClip
    {
        public float3 Direction;
        public float Strength;
        public TlCurve Falloff;
    }

    public struct SpikeForce
    {
        public float3 Value;
    }

    public static class SpikeForceMath
    {
        public static float3 Evaluate(in SpikeForceClip clip, float progress) => clip.Direction * clip.Strength * clip.Falloff.Evaluate(progress);
    }
}
