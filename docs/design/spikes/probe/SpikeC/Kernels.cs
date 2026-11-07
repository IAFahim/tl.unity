using Tl;
using Tl.Spike;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using U = Unity.Mathematics;
using T = Tl.Mathematics;

namespace SpikeC
{
    public static class Probe
    {
        public static int Managed;
        [BurstDiscard]
        public static void Mark() => Managed++;
    }

    public struct UClip
    {
        public U.float3 Direction;
        public float Strength;
        public TlCurve Falloff;
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct UEvaluate : IJobFor
    {
        [ReadOnly] public NativeArray<UClip> Clips;
        public NativeArray<U.float3> Force;
        public float Progress;

        public void Execute(int i)
        {
            var c = Clips[i];
            Force[i] += c.Direction * c.Strength * c.Falloff.Evaluate(Progress);
            if (i == 0) Probe.Mark();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct TEvaluate : IJobFor
    {
        [ReadOnly] public NativeArray<SpikeForceClip> Clips;
        public NativeArray<T.float3> Force;
        public float Progress;

        public void Execute(int i)
        {
            var c = Clips[i];
            Force[i] += c.Direction * c.Strength * c.Falloff.Evaluate(Progress);
            if (i == 0) Probe.Mark();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct UApply : IJobFor
    {
        [ReadOnly] public NativeArray<U.float3> Total;
        public NativeArray<U.float3> Force;

        public void Execute(int i)
        {
            var total = default(U.float3);
            total += Total[i];
            total += Total[i] * 0.5f;
            Force[i] += total;
            if (i == 0) Probe.Mark();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct TApply : IJobFor
    {
        [ReadOnly] public NativeArray<T.float3> Total;
        public NativeArray<T.float3> Force;

        public void Execute(int i)
        {
            var total = default(T.float3);
            total += Total[i];
            total += Total[i] * 0.5f;
            Force[i] += total;
            if (i == 0) Probe.Mark();
        }
    }
}
