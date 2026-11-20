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

    static unsafe class DueScan
    {
        public static int Collect(ushort* dues, int count, ushort now, byte* hits, ref int near, ref bool live)
        {
            var held = 0;
            var i = 0;
            var calmest = (int)TimelineTicks.Mask;
            if (global::Unity.Burst.Intrinsics.X86.Avx2.IsAvx2Supported)
            {
                var bias = new global::Unity.Burst.Intrinsics.v256((ushort)(now + 1));
                var mask = new global::Unity.Burst.Intrinsics.v256(TimelineTicks.Mask);
                var edge = new global::Unity.Burst.Intrinsics.v256((ushort)TimelineTicks.MaxCalm);
                var least = mask;
                for (; i + 16 <= count; i += 16)
                {
                    var due = global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(dues + i);
                    var inert = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_srai_epi16(due, 15);
                    var ahead = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_sub_epi16(due, bias), mask);
                    var late = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpgt_epi16(ahead, edge);
                    var hit = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_andnot_si256(inert, late);
                    least = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_min_epu16(least, global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_or_si256(ahead, global::Unity.Burst.Intrinsics.X86.Avx2.mm256_or_si256(inert, late)), mask));
                    var bits = (uint)global::Unity.Burst.Intrinsics.X86.Avx2.mm256_movemask_epi8(hit) & 0x55555555u;
                    while (bits != 0)
                    {
                        hits[held++] = (byte)(i + (global::Unity.Mathematics.math.tzcnt(bits) >> 1));
                        bits &= bits - 1;
                    }
                }
                calmest = Least(least.Lo128, Least(least.Hi128, calmest));
            }
            else if (global::Unity.Burst.Intrinsics.X86.Sse2.IsSse2Supported)
            {
                var bias = new global::Unity.Burst.Intrinsics.v128((ushort)(now + 1));
                var mask = new global::Unity.Burst.Intrinsics.v128(TimelineTicks.Mask);
                var edge = new global::Unity.Burst.Intrinsics.v128((ushort)TimelineTicks.MaxCalm);
                var least = mask;
                for (; i + 8 <= count; i += 8)
                {
                    var due = global::Unity.Burst.Intrinsics.X86.Sse2.loadu_si128(dues + i);
                    var inert = global::Unity.Burst.Intrinsics.X86.Sse2.srai_epi16(due, 15);
                    var ahead = global::Unity.Burst.Intrinsics.X86.Sse2.and_si128(global::Unity.Burst.Intrinsics.X86.Sse2.sub_epi16(due, bias), mask);
                    var late = global::Unity.Burst.Intrinsics.X86.Sse2.cmpgt_epi16(ahead, edge);
                    var hit = global::Unity.Burst.Intrinsics.X86.Sse2.andnot_si128(inert, late);
                    least = global::Unity.Burst.Intrinsics.X86.Sse2.min_epi16(least, global::Unity.Burst.Intrinsics.X86.Sse2.and_si128(global::Unity.Burst.Intrinsics.X86.Sse2.or_si128(ahead, global::Unity.Burst.Intrinsics.X86.Sse2.or_si128(inert, late)), mask));
                    var bits = (uint)global::Unity.Burst.Intrinsics.X86.Sse2.movemask_epi8(hit) & 0x5555u;
                    while (bits != 0)
                    {
                        hits[held++] = (byte)(i + (global::Unity.Mathematics.math.tzcnt(bits) >> 1));
                        bits &= bits - 1;
                    }
                }
                calmest = Least(least, calmest);
            }
            for (; i < count; i++)
            {
                var due = dues[i];
                if ((due & TimelineTicks.Inert) != 0) continue;
                var ahead = (due - now - 1) & TimelineTicks.Mask;
                if (ahead > TimelineTicks.MaxCalm) hits[held++] = (byte)i;
                else if (ahead < calmest) calmest = ahead;
            }
            if (calmest < TimelineTicks.Mask)
            {
                live = true;
                if (calmest + 1 < near) near = calmest + 1;
            }
            return held;
        }

        static int Least(global::Unity.Burst.Intrinsics.v128 lanes, int least)
        {
            if (lanes.UShort0 < least) least = lanes.UShort0;
            if (lanes.UShort1 < least) least = lanes.UShort1;
            if (lanes.UShort2 < least) least = lanes.UShort2;
            if (lanes.UShort3 < least) least = lanes.UShort3;
            if (lanes.UShort4 < least) least = lanes.UShort4;
            if (lanes.UShort5 < least) least = lanes.UShort5;
            if (lanes.UShort6 < least) least = lanes.UShort6;
            if (lanes.UShort7 < least) least = lanes.UShort7;
            return least;
        }
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
