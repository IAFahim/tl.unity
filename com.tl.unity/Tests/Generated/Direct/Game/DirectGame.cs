using Tl;
using Unity.Entities;

namespace DirectGame
{
    public struct Stride : IComponentData
    {
        public float X;
        public float Y;
    }

    public struct StrideStat : IComponentData
    {
        public float Value;
    }

    public struct Pace
    {
        public float Value;
        public byte Priority;
    }

    public struct StrideClip
    {
        public float Speed;
        public byte Priority;
    }

    public struct StrideTrack : IBlend<StrideClip>
    {
        public float Scale;

        public void Blend(in StrideClip first, in StrideClip second, float factor, out StrideClip result) => result = first;
    }

    [TimelineKind]
    public struct Pacing
    {
        public static void Combine(ref Pace total, in Pace next)
        {
            if (next.Priority >= total.Priority)
                total = next;
        }

        public static void Apply(in Pace total, ref Stride stride) => stride.X += total.Value;
    }

    public static class KindCapacity
    {
        public const int Pacing = 2;
    }

    public struct Striding : ITrack<StrideTrack, StrideClip>
    {
        [Feeds(typeof(Pacing))]
        public static Pace Evaluate(in Frame<StrideTrack, StrideClip> f, in StrideStat stat)
            => new Pace { Value = f.Direction * f.Clip.Speed * f.Track.Scale * stat.Value, Priority = f.Clip.Priority };
    }
}
