using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace KindPure.Origins
{
    public struct XorShift64
    {
        public ulong State;

        public XorShift64(ulong seed)
        {
            State = seed;
        }

        public uint Next()
        {
            State ^= State << 13;
            State ^= State >> 7;
            State ^= State << 17;
            return (uint)State;
        }
    }

    public unsafe struct EndedSlots
    {
        public const int Words = 4;
        public fixed ulong Mask[Words];
    }

    public unsafe struct OriginTable : IDisposable
    {
        public const int Timelines = 500;
        public const int AddressableRows = 65536;
        public const int MaxActive = EndedSlots.Words * 64;
        const int LanesPerRow = 8;
        const int HeadLane = 0;
        const int SpeedLane = 1;
        const int PickLane = 2;
        const int LiftLane = 3;
        const int DamageLane = 4;
        const int StatLane = 5;
        const int EndLane = 6;
        const int PadLane = 7;
        const int WalkBit = 1;
        const int LiftBit = 2;

        [NativeDisableUnsafePtrRestriction] public int* Rows;
        [NativeDisableUnsafePtrRestriction] public int* DurationOfTimeline;
        [NativeDisableUnsafePtrRestriction] public int* FirstRowOfTimeline;
        [NativeDisableUnsafePtrRestriction] public short* TimelineOfRow;
        [NativeDisableUnsafePtrRestriction] public int* ManagedRuns;

        public OriginTable(ulong rowSeed)
        {
            Rows = AllocateCleared<int>(AddressableRows * LanesPerRow);
            DurationOfTimeline = AllocateCleared<int>(Timelines);
            FirstRowOfTimeline = AllocateCleared<int>(Timelines);
            TimelineOfRow = AllocateCleared<short>(AddressableRows);
            ManagedRuns = AllocateCleared<int>(1);
            var nextFreeRow = 0;
            for (var timeline = 0; timeline < Timelines; timeline++)
            {
                DurationOfTimeline[timeline] = 30 + timeline * 7 % 31;
                FirstRowOfTimeline[timeline] = nextFreeRow;
                nextFreeRow += DurationOfTimeline[timeline];
            }
            for (var timeline = 0; timeline < Timelines; timeline++)
                for (var tick = 0; tick < DurationOfTimeline[timeline]; tick++)
                    TimelineOfRow[FirstRowOfTimeline[timeline] + tick] = (short)timeline;
            var random = new XorShift64(rowSeed);
            for (var timeline = 0; timeline < Timelines; timeline++)
                for (var tick = 0; tick < DurationOfTimeline[timeline]; tick++)
                    WriteRow(ref random, Rows + (FirstRowOfTimeline[timeline] + tick) * LanesPerRow, tick == DurationOfTimeline[timeline] - 1);
        }

        static T* AllocateCleared<T>(int count) where T : unmanaged
        {
            var bytes = (long)count * sizeof(T);
            var memory = (T*)UnsafeUtility.Malloc(bytes, 64, Allocator.Persistent);
            UnsafeUtility.MemClear(memory, bytes);
            return memory;
        }

        static void WriteRow(ref XorShift64 random, int* row, bool lastTick)
        {
            var head = 0;
            var speed = 0f;
            var priority = 0;
            var lift = 0f;
            var damage = 0;
            if (random.Next() % 3 != 0)
            {
                head |= WalkBit;
                speed = random.Next() % 64 * 0.25f;
                priority = (int)(random.Next() % 4);
            }
            if (random.Next() % 2 != 0)
            {
                head |= LiftBit;
                lift = ((int)(random.Next() % 33) - 16) * 0.125f + 0.5f;
            }
            var stat = random.Next() % 5 == 0 ? (int)(random.Next() % 7) - 3 : 0;
            if (random.Next() % 8 == 0) damage = (int)(1 + random.Next() % 9);
            row[HeadLane] = head;
            ((float*)row)[SpeedLane] = (head & WalkBit) != 0 ? speed : 0f;
            row[PickLane] = (head & WalkBit) != 0 ? priority << 8 : -1;
            ((float*)row)[LiftLane] = (head & LiftBit) != 0 ? lift : 0f;
            row[DamageLane] = damage;
            row[StatLane] = stat;
            row[EndLane] = lastTick ? 1 : 0;
            row[PadLane] = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int* RowAt(ushort origin, uint frame) => Rows + ((ushort)(origin + frame) << 3);

        [BurstDiscard]
        void MarkManagedRun()
        {
            *ManagedRuns = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ushort* origins, int active, uint frame, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            MarkManagedRun();
            var ended = new EndedSlots();
            float lift;
            int damage;
            int stat;
            int pickKey;
            if (X86.Avx2.IsAvx2Supported)
            {
                var floatSums = X86.Avx.mm256_setzero_ps();
                var intSums = X86.Avx.mm256_setzero_si256();
                var pickKeys = X86.Avx.mm256_set1_epi32(-1);
                for (var slot = 0; slot < active; slot++)
                {
                    var row = RowAt(origins[slot], frame);
                    var lanes = X86.Avx.mm256_load_si256(row);
                    floatSums = X86.Avx.mm256_add_ps(floatSums, lanes);
                    intSums = X86.Avx2.mm256_add_epi32(intSums, lanes);
                    pickKeys = X86.Avx2.mm256_max_epi32(pickKeys, X86.Avx2.mm256_or_si256(lanes, X86.Avx.mm256_set1_epi32(slot)));
                    ended.Mask[slot >> 6] |= (ulong)(uint)row[EndLane] << (slot & 63);
                }
                lift = floatSums.Float3;
                damage = intSums.SInt4;
                stat = intSums.SInt5;
                pickKey = pickKeys.SInt2;
            }
            else
            {
                lift = 0f;
                damage = 0;
                stat = 0;
                pickKey = -1;
                for (var slot = 0; slot < active; slot++)
                {
                    var row = RowAt(origins[slot], frame);
                    lift += ((float*)row)[LiftLane];
                    damage += row[DamageLane];
                    stat += row[StatLane];
                    pickKey = math.max(pickKey, row[PickLane] | slot);
                    ended.Mask[slot >> 6] |= (ulong)(uint)row[EndLane] << (slot & 63);
                }
            }
            var speed = pickKey >= 0 ? ((float*)RowAt(origins[pickKey & 255], frame))[SpeedLane] : 0f;
            motion.X += speed * stats.Walk;
            motion.Y += lift * stats.Jump;
            vitals.Health -= damage;
            vitals.Stamina += stat;
            for (var word = 0; word < EndedSlots.Words; word++)
                for (var endedSlots = ended.Mask[word]; endedSlots != 0; endedSlots &= endedSlots - 1)
                {
                    var slot = word * 64 + math.tzcnt(endedSlots);
                    origins[slot] = (ushort)(origins[slot] - DurationOfTimeline[TimelineOfRow[(ushort)(origins[slot] + frame)]]);
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadOnlyFloor(ushort* origins, int active, uint frame, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            MarkManagedRun();
            float lift;
            int damage;
            int stat;
            int pickLane;
            if (X86.Avx2.IsAvx2Supported)
            {
                var floatSums = X86.Avx.mm256_setzero_ps();
                var intSums = X86.Avx.mm256_setzero_si256();
                var maxLanes = X86.Avx.mm256_set1_epi32(-1);
                for (var slot = 0; slot < active; slot++)
                {
                    var lanes = X86.Avx.mm256_load_si256(RowAt(origins[slot], frame));
                    floatSums = X86.Avx.mm256_add_ps(floatSums, lanes);
                    intSums = X86.Avx2.mm256_add_epi32(intSums, lanes);
                    maxLanes = X86.Avx2.mm256_max_epi32(maxLanes, lanes);
                }
                lift = floatSums.Float3;
                damage = intSums.SInt4;
                stat = intSums.SInt5;
                pickLane = maxLanes.SInt2;
            }
            else
            {
                lift = 0f;
                damage = 0;
                stat = 0;
                pickLane = -1;
                for (var slot = 0; slot < active; slot++)
                {
                    var row = RowAt(origins[slot], frame);
                    lift += ((float*)row)[LiftLane];
                    damage += row[DamageLane];
                    stat += row[StatLane];
                    pickLane = math.max(pickLane, row[PickLane]);
                }
            }
            motion.X += pickLane * stats.Walk;
            motion.Y += lift * stats.Jump;
            vitals.Health -= damage;
            vitals.Stamina += stat;
        }

        public void Dispose()
        {
            UnsafeUtility.Free(Rows, Allocator.Persistent);
            UnsafeUtility.Free(DurationOfTimeline, Allocator.Persistent);
            UnsafeUtility.Free(FirstRowOfTimeline, Allocator.Persistent);
            UnsafeUtility.Free(TimelineOfRow, Allocator.Persistent);
            UnsafeUtility.Free(ManagedRuns, Allocator.Persistent);
        }
    }
}
