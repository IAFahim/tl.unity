using System;
using Tl;

namespace GeneratedGame
{
    public struct Position { public float X; }
    public struct Heading { public float Degrees; }

    public struct GustClip { public float Force; }
    public struct GustTrack : IBlend<GustClip>
    {
        public void Blend(in GustClip first, in GustClip second, float factor, out GustClip result) => result = first;
    }

    [TimelineKind(Accepts = KindEntries.Pulses)]
    public struct Gust
    {
        public static void Combine(ref float total, in float next) => total += next;
        public static void Apply(in float total, ref Position position) => position.X += total;
    }

    [TimelineSystemMember(typeof(Drift))]
    public struct Blow : ITrack<GustTrack, GustClip>
    {
        [Pulse(typeof(Gust), On = ClipEvent.Start)]
        public static float Start(in Frame<GustTrack, GustClip> f) => f.Clip.Force;
    }

    [TimelineSystem]
    public ref partial struct Drift
    {
        public Span<Position> Position;
        public ReadOnlySpan<Heading> Heading;
    }
}
