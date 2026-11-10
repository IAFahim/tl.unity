using System;
using System.Runtime.InteropServices;

namespace Tl;

public enum Direction : sbyte
{
    Forward = 1,
    Reverse = -1,
}

public enum Edge : byte
{
    None = 0,
    Wrapped = 1,
    Ended = 2,
    Started = 3,
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct Row : IEquatable<Row>
{
    public readonly int Value;

    public Row(int value)
    {
        Value = value;
    }

    public bool Equals(Row other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is Row other && other.Value == Value;

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(Row left, Row right) => left.Value == right.Value;

    public static bool operator !=(Row left, Row right) => left.Value != right.Value;
}

public static class Clock
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Playback
    {
        public ushort Phase;
        public ushort Duration;
        public bool Looping;
    }

    public static (Playback Playback, Edge Edge) Step(Playback playback, Direction direction)
    {
        ushort duration = playback.Duration;
        if (duration == 0)
        {
            playback.Phase = 0;
            return (playback, direction == Direction.Forward ? Edge.Ended : Edge.Started);
        }
        uint last = (uint)duration - 1u;
        uint current = playback.Phase;
        if (current > last)
            current = last;
        if (direction == Direction.Forward)
        {
            if (current < last)
            {
                playback.Phase = (ushort)(current + 1u);
                return (playback, Edge.None);
            }
            if (playback.Looping)
            {
                playback.Phase = 0;
                return (playback, Edge.Wrapped);
            }
            playback.Phase = (ushort)last;
            return (playback, Edge.Ended);
        }
        if (current > 0u)
        {
            playback.Phase = (ushort)(current - 1u);
            return (playback, Edge.None);
        }
        if (playback.Looping)
        {
            playback.Phase = (ushort)last;
            return (playback, Edge.Wrapped);
        }
        playback.Phase = 0;
        return (playback, Edge.Started);
    }
}
