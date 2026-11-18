using System;
using Tl;

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
            ManagedProbe.Mark();
            return frame.Direction * (frame.Clip.Value * frame.Track.Factor + (frame.Tick & 31) * 0.25f);
        }
    }

    public static class ManagedProbe
    {
        public static int Hits;
        [Unity.Burst.BurstDiscard] public static void Mark() { Hits = 1; }
    }
}
