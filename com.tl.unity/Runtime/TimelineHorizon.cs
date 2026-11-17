using System.Runtime.CompilerServices;
using Unity.Entities;

namespace Tl.Unity
{
    public struct TimelineHorizon : IComponentData
    {
        public byte Steps;
    }

    public struct TimelineGlide : IComponentData
    {
        public byte Limit;

        public static TimelineGlide Default => new TimelineGlide { Limit = global::Tl.Horizon.Max };
    }

    public struct TimelineClock : IComponentData
    {
        public ushort Value;
    }

    [InternalBufferCapacity(4)]
    public struct PlaybackDue : IBufferElementData
    {
        public ushort Tick;
    }

    public static unsafe class TimelineTicks
    {
        public const ushort Inert = 0x8000;
        public const ushort Mask = 0x7FFF;
        public const int MaxCalm = 0x3FFE;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Real(global::Tl.Playback* playback, ushort clock)
        {
            if (global::Tl.SlotWord.PlainMask(*(ulong*)playback) == 0) return playback->Tick;
            return (playback->Flags & global::Tl.PlaybackFlags.Reverse) != 0 ? (ushort)(playback->Tick - clock) : (ushort)(playback->Tick + clock);
        }

        public static ushort Real(in Playback playback, ushort clock)
        {
            var copy = playback;
            return Real((global::Tl.Playback*)&copy, clock);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Decode(global::Tl.Playback* slot, ushort clock) => slot->Tick = Real(slot, clock);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(global::Tl.Playback* slot, ushort clock)
        {
            if (global::Tl.SlotWord.PlainMask(*(ulong*)slot) == 0) return;
            slot->Tick = (slot->Flags & global::Tl.PlaybackFlags.Reverse) != 0 ? (ushort)(slot->Tick + clock) : (ushort)(slot->Tick - clock);
        }

        public static void Decode(global::Tl.Playback* slots, int count, ushort clock)
        {
            for (var i = 0; i < count; i++) Decode(slots + i, clock);
        }

        public static void Encode(global::Tl.Playback* slots, int count, ushort clock)
        {
            for (var i = 0; i < count; i++) Encode(slots + i, clock);
        }

        public static ushort Due(global::Tl.Playback* slot, in global::Tl.KindTables tables, in global::Tl.KindCalm calm, ulong worldReverse, ushort now)
        {
            var word = *(ulong*)slot;
            if ((word & global::Tl.SlotWord.TimelineMask) == 0 || global::Tl.SlotWord.IsInert(word)) return Inert;
            if (global::Tl.SlotWord.PlainMask(word) == 0) return (ushort)((now + 1) & Mask);
            var index = slot->Timeline - 1;
            int tick = slot->Tick;
            var duration = index < tables.TimelineCount ? (int)tables.Timelines[index].Duration : 0;
            var steps = 0;
            if (tick < duration)
            {
                var up = global::Tl.SlotWord.MovesForward(word, worldReverse);
                var at = calm.Start == null ? -1 : calm.Start[index];
                steps = at >= 0 ? (up ? calm.Forward : calm.Backward)[at + tick] : up ? duration - 1 - tick : tick;
                if (steps > MaxCalm) steps = MaxCalm;
            }
            return (ushort)((now + 1 + steps) & Mask);
        }
    }
}
