using System;
using Tl;
using Unity.Burst;

namespace ScaleGame
{
    [TimelineSystem]
    public ref partial struct Scale
    {
        public Span<float> Total;
    }

    [TimelineKind]
    public struct Energy
    {
        public static void Combine(ref float total, in float next) => total += next;
        public static void Apply(in float total, ref float output) => output += total;
    }

    public struct ScaleClip
    {
        public float Value;
    }

    public struct ScaleTrack : IBlend<ScaleClip>
    {
        public float Factor;
        public void Blend(in ScaleClip first, in ScaleClip second, float factor, out ScaleClip result) => result = first;
    }

    [TimelineSystemMember(typeof(Scale))]
    public struct Drive : ITrack<ScaleTrack, ScaleClip>
    {
        [Feeds(typeof(Energy))]
        public static float Evaluate(in Frame<ScaleTrack, ScaleClip> frame)
        {
            ScaleProbe.MarkManaged();
            return frame.Direction * (frame.Clip.Value * frame.Track.Factor + (frame.Tick & 31) * 0.25f);
        }
    }

    public static class KindCapacity
    {
        public const int Energy = 4;
    }

    public static class ScaleProbe
    {
        public static int ManagedEvaluations;

        [BurstDiscard]
        public static void MarkManaged() => System.Threading.Interlocked.Increment(ref ManagedEvaluations);
    }
}
