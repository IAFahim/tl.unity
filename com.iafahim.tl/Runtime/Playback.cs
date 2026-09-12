using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    ClipStart = 1 << 0,
    ClipEnd = 1 << 1,
    TimelineStart = 1 << 2,
    TimelineEnd = 1 << 3,
    CompletedBefore = 1 << 4,
    CompletedAfter = 1 << 5,
    Looping = 1 << 6,
    Reverse = 1 << 7,
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct TimelineState
{
    public readonly uint Asset;
    public readonly uint Position;
    public readonly long Cycle;

    public TimelineState(uint asset, uint position = 0, long cycle = 0)
    {
        Asset = asset;
        Position = position;
        Cycle = cycle;
    }
}

public static class TimelineMovement
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Advance(
        uint duration,
        bool looping,
        bool reverse,
        uint position,
        long inCycle,
        out uint nextPosition,
        out long nextCycle,
        out uint tick,
        out long outCycle,
        out FrameFlags flags)
    {
        nextPosition = position; nextCycle = inCycle; outCycle = 0; tick = 0; flags = FrameFlags.None;
        if (duration == 0 || position > duration || looping && position == duration) return false;
        if (looping)
        {
            flags = reverse ? FrameFlags.Looping | FrameFlags.Reverse : FrameFlags.Looping;
            if (reverse)
            {
                if (position == 0) { tick = duration - 1u; outCycle = unchecked(inCycle - 1L); }
                else { tick = position - 1u; outCycle = inCycle; }
                nextPosition = tick; nextCycle = outCycle;
            }
            else
            {
                tick = position; outCycle = inCycle;
                nextPosition = tick == duration - 1u ? 0 : tick + 1u;
                nextCycle = tick == duration - 1u ? unchecked(inCycle + 1L) : inCycle;
            }
            if (tick == 0) flags |= FrameFlags.TimelineStart;
            if (tick == duration - 1u) flags |= FrameFlags.TimelineEnd;
            return true;
        }
        nextCycle = 0;
        if (reverse)
        {
            if (position == 0) return false;
            tick = position - 1u; flags = FrameFlags.Reverse;
            if (tick == 0) flags |= FrameFlags.TimelineStart;
            if (tick == duration - 1u) flags |= FrameFlags.TimelineEnd;
            if (position == duration) flags |= FrameFlags.CompletedBefore;
            nextPosition = tick;
            return true;
        }
        if (position == duration) return false;
        tick = position;
        if (tick == 0) flags = FrameFlags.TimelineStart;
        if (tick == duration - 1u) flags |= FrameFlags.TimelineEnd | FrameFlags.CompletedAfter;
        nextPosition = tick + 1u;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Select(
        in TimelineState state,
        uint duration,
        bool looping,
        bool reverse,
        out TimelineState next,
        out uint tick,
        out long cycle,
        out FrameFlags flags)
    {
        next = state; tick = 0; cycle = 0; flags = FrameFlags.None;
        if (state.Asset == 0 || !Advance(duration, looping, reverse, state.Position, state.Cycle, out var np, out var nc, out tick, out cycle, out flags))
            return false;
        next = new TimelineState(state.Asset, np, nc);
        return true;
    }
}
