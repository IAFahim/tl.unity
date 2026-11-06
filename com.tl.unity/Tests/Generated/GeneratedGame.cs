using System;
using Tl;

namespace GeneratedGame
{
    public struct Position { public float X; }
    public struct Heading { public float Degrees; }

    [TimelineSystem]
    public ref partial struct Drift
    {
        public Span<Position> Position;
        public ReadOnlySpan<Heading> Heading;
    }
}