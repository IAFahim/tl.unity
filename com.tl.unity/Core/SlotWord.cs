using System.Runtime.CompilerServices;

namespace Tl;

public static class SlotWord
{
    public const ulong TimelineMask = 0xFFFFul;
    public const ulong OneTick = 1ul << 16;
    public const ulong MinusOneTick = ~OneTick + 1;
    public const ulong ReverseBit = (ulong)PlaybackFlags.Reverse << 40;
    public const ulong NotPlainMask = 0x00FF_FE00_0000_0000ul;
    public const ulong InertAnyMask = (ulong)(PlaybackFlags.Paused | PlaybackFlags.Ended) << 40;
    public const ulong InertNoneMask = (ulong)(PlaybackFlags.StartPending | PlaybackFlags.Stopping | PlaybackFlags.StopAged | PlaybackFlags.Began | PlaybackFlags.ReenterPending) << 40;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong WorldReverse(bool forward) => forward ? 0ul : ReverseBit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong PlainMask(ulong slot)
        => ((slot & NotPlainMask) == 0) & ((slot & TimelineMask) != 0) ? ulong.MaxValue : 0ul;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInert(ulong slot)
        => (slot & InertAnyMask) != 0 && (slot & InertNoneMask) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MovesForward(ulong slot, ulong worldReverse) => ((slot ^ worldReverse) & ReverseBit) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Glide(ulong slot, ulong worldReverse)
    {
        var step = MovesForward(slot, worldReverse) ? OneTick : MinusOneTick;
        return unchecked(slot + (step & PlainMask(slot)));
    }
}
