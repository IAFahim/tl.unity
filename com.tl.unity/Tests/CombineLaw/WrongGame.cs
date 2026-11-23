using System;
using Tl;

namespace CombineLaw
{
    public struct Seen { public int A; public int B; public int C; }
    public struct Pri { public int Value; public byte Priority; }
    public struct WrongClip { public int V; public int P; }
    public struct WrongTrack : IBlend<WrongClip>
    {
        public float Scale;
        public void Blend(in WrongClip first, in WrongClip second, float factor, out WrongClip result) => result = first;
    }

    [TimelineKind(Combine = KindCombine.Sum)]
    public struct SumIsMax
    {
        public static void Combine(ref int total, in int next) => total = Math.Max(total, next);
        public static void Apply(in int total, ref Seen seen) => seen.A += total;
    }

    [TimelineKind(Combine = KindCombine.Pick, Priority = nameof(Pri.Priority))]
    public struct FirstWins
    {
        public static void Combine(ref Pri total, in Pri next) { if (next.Priority > total.Priority) total = next; }
        public static void Apply(in Pri total, ref Seen seen) => seen.B += total.Value;
    }

    [TimelineKind(Combine = KindCombine.Or)]
    public struct OrIsSum
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int total, ref Seen seen) => seen.C += total;
    }

    [TimelineKind(Combine = KindCombine.Sum)]
    public struct SumRight
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int total, ref Seen seen) => seen.A += total;
    }

    [TimelineKind(Combine = KindCombine.Pick, Priority = nameof(Pri.Priority))]
    public struct PickRight
    {
        public static void Combine(ref Pri total, in Pri next) { if (next.Priority >= total.Priority) total = next; }
        public static void Apply(in Pri total, ref Seen seen) => seen.B += total.Value;
    }

    [TimelineKind(Combine = KindCombine.Or)]
    public struct OrRight
    {
        public static void Combine(ref int total, in int next) => total |= next;
        public static void Apply(in int total, ref Seen seen) => seen.C += total;
    }

    [TimelineSystemMember(typeof(SumSystem))]
    public struct ToSum : ITrack<WrongTrack, WrongClip>
    {
        [Feeds(typeof(SumIsMax))]
        public static int V(in Frame<WrongTrack, WrongClip> f) => f.Clip.V;
    }

    [TimelineSystemMember(typeof(PickSystem))]
    public struct ToPick : ITrack<WrongTrack, WrongClip>
    {
        [Feeds(typeof(FirstWins))]
        public static Pri V(in Frame<WrongTrack, WrongClip> f) => new Pri { Value = f.Clip.V, Priority = (byte)f.Clip.P };
    }

    [TimelineSystemMember(typeof(OrSystem))]
    public struct ToOr : ITrack<WrongTrack, WrongClip>
    {
        [Feeds(typeof(OrIsSum))]
        public static int V(in Frame<WrongTrack, WrongClip> f) => f.Clip.V;
    }

    [TimelineSystemMember(typeof(RightSystem))]
    public struct ToRightSum : ITrack<WrongTrack, WrongClip>
    {
        [Feeds(typeof(SumRight))]
        public static int V(in Frame<WrongTrack, WrongClip> f) => f.Clip.V;
    }

    [TimelineSystemMember(typeof(RightSystem))]
    public struct ToRightPick : ITrack<WrongTrack, WrongClip>
    {
        [Feeds(typeof(PickRight))]
        public static Pri V(in Frame<WrongTrack, WrongClip> f) => new Pri { Value = f.Clip.V, Priority = (byte)f.Clip.P };
    }

    [TimelineSystemMember(typeof(RightSystem))]
    public struct ToRightOr : ITrack<WrongTrack, WrongClip>
    {
        [Feeds(typeof(OrRight))]
        public static int V(in Frame<WrongTrack, WrongClip> f) => f.Clip.V;
    }

    [TimelineSystem]
    public ref partial struct SumSystem
    {
        public Span<Seen> Seen;
    }

    [TimelineSystem]
    public ref partial struct PickSystem
    {
        public Span<Seen> Seen;
    }

    [TimelineSystem]
    public ref partial struct OrSystem
    {
        public Span<Seen> Seen;
    }

    [TimelineSystem]
    public ref partial struct RightSystem
    {
        public Span<Seen> Seen;
    }
}
