using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace KindPure.Origins
{
    public unsafe struct Pad2 : IComponentData { public fixed byte Bytes[2]; }
    public unsafe struct Pad4 : IComponentData { public fixed byte Bytes[4]; }
    public unsafe struct Pad8 : IComponentData { public fixed byte Bytes[8]; }
    public unsafe struct Pad16 : IComponentData { public fixed byte Bytes[16]; }
    public unsafe struct Pad32 : IComponentData { public fixed byte Bytes[32]; }
    public unsafe struct Pad64 : IComponentData { public fixed byte Bytes[64]; }
    public unsafe struct Pad128 : IComponentData { public fixed byte Bytes[128]; }

    public struct CostDense : IComponentData, IEnableableComponent
    {
        public ushort Next;
        public byte Count;
    }

    public unsafe struct CostDues : IComponentData
    {
        public fixed ushort Due[4];
    }

    [InternalBufferCapacity(0)]
    public struct CostSpillDue : IBufferElementData
    {
        public ushort Value;
    }

    public struct CostPlaying : IComponentData, IEnableableComponent { }
    public struct CostActiveA : IComponentData, IEnableableComponent { }
    public struct CostEnabledA : IComponentData, IEnableableComponent { }
    public struct CostActiveB : IComponentData, IEnableableComponent { }
    public struct CostEnabledB : IComponentData, IEnableableComponent { }

    public struct CostHit
    {
        public ArchetypeChunk Chunk;
        public Entity Entity;
        public int Row;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public unsafe struct CostLane
    {
        public CostHit* Items;
        public int Count;
        public int Capacity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(in ArchetypeChunk chunk, Entity entity, int row, int value)
        {
            if (Count == Capacity) Grow();
            Items[Count++] = new CostHit { Chunk = chunk, Entity = entity, Row = row, Value = value };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow()
        {
            var next = Capacity == 0 ? 1024 : Capacity * 2;
            var block = (CostHit*)UnsafeUtility.Malloc((long)next * sizeof(CostHit), 64, Allocator.Persistent);
            if (Items != null)
            {
                UnsafeUtility.MemCpy(block, Items, (long)Count * sizeof(CostHit));
                UnsafeUtility.Free(Items, Allocator.Persistent);
            }
            Items = block;
            Capacity = next;
        }
    }

    public enum CostLevel
    {
        U1,
        Chunk,
        Dues,
        Walks,
        Checks,
        ScalarPick,
        TwoKinds,
        SlotFree,
        LanePulses,
        Composite,
        CompositeSlotFree,
    }

    public unsafe struct CostPlan : IDisposable
    {
        [NativeDisableUnsafePtrRestriction] public int* Rows;
        [NativeDisableUnsafePtrRestriction] public ushort* Ahead;
        [NativeDisableUnsafePtrRestriction] public ushort* Gap;
        [NativeDisableUnsafePtrRestriction] public short* Shift;

        public CostPlan(in OriginTable table, bool endsOnly)
        {
            Rows = table.Rows;
            Ahead = (ushort*)UnsafeUtility.Malloc(OriginTable.AddressableRows * 2, 64, Allocator.Persistent);
            Gap = (ushort*)UnsafeUtility.Malloc(OriginTable.AddressableRows * 2, 64, Allocator.Persistent);
            Shift = (short*)UnsafeUtility.Malloc(OriginTable.AddressableRows * 2, 64, Allocator.Persistent);
            UnsafeUtility.MemClear(Ahead, OriginTable.AddressableRows * 2);
            UnsafeUtility.MemClear(Gap, OriginTable.AddressableRows * 2);
            UnsafeUtility.MemClear(Shift, OriginTable.AddressableRows * 2);
            for (var t = 0; t < OriginTable.Timelines; t++)
            {
                var first = table.FirstRowOfTimeline[t];
                var duration = table.DurationOfTimeline[t];
                for (var tick = 0; tick < duration; tick++)
                {
                    var distance = 0;
                    while (!IsEvent(Rows + (first + (tick + distance) % duration) * 8, endsOnly)) distance++;
                    Ahead[first + tick] = (ushort)distance;
                }
                for (var tick = 0; tick < duration; tick++)
                {
                    var end = tick == duration - 1;
                    Shift[first + tick] = end ? (short)-duration : (short)0;
                    Gap[first + tick] = (ushort)(1 + Ahead[first + (tick + 1) % duration]);
                }
            }
        }

        static bool IsEvent(int* lanes, bool endsOnly) => endsOnly ? lanes[6] != 0 : (lanes[4] | lanes[5] | lanes[6]) != 0;

        public void Dispose()
        {
            UnsafeUtility.Free(Ahead, Allocator.Persistent);
            UnsafeUtility.Free(Gap, Allocator.Persistent);
            UnsafeUtility.Free(Shift, Allocator.Persistent);
        }
    }

    public static unsafe class CostKernels
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Move(int* rows, ushort* origins, int active, uint frame, ref Motion motion, in Stats stats)
        {
            float lift;
            int pickKey;
            uint any;
            if (X86.Avx2.IsAvx2Supported)
            {
                var floatSums = X86.Avx.mm256_setzero_ps();
                var pickKeys = X86.Avx.mm256_set1_epi32(-1);
                var heads = X86.Avx.mm256_setzero_si256();
                for (var slot = 0; slot < active; slot++)
                {
                    var lanes = X86.Avx.mm256_load_si256(rows + ((ushort)(origins[slot] + frame) << 3));
                    floatSums = X86.Avx.mm256_add_ps(floatSums, lanes);
                    pickKeys = X86.Avx2.mm256_max_epi32(pickKeys, X86.Avx2.mm256_or_si256(lanes, X86.Avx.mm256_set1_epi32(slot)));
                    heads = X86.Avx2.mm256_or_si256(heads, lanes);
                }
                lift = floatSums.Float3;
                pickKey = pickKeys.SInt2;
                any = heads.UInt0;
            }
            else
            {
                lift = 0f;
                pickKey = -1;
                any = 0u;
                for (var slot = 0; slot < active; slot++)
                {
                    var row = rows + ((ushort)(origins[slot] + frame) << 3);
                    lift += ((float*)row)[3];
                    pickKey = math.max(pickKey, row[2] | slot);
                    any |= (uint)row[0];
                }
            }
            var speed = pickKey >= 0 ? ((float*)(rows + ((ushort)(origins[pickKey & 255] + frame) << 3)))[1] : 0f;
            motion.X += speed * stats.Walk;
            motion.Y += lift * stats.Jump;
            return any;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint MoveLanes(int* rows, ushort* origins, int active, uint frame, ref Motion motion, in Stats stats, ref Vitals vitals)
        {
            float lift;
            int pickKey;
            uint any;
            int damage;
            int stat;
            if (X86.Avx2.IsAvx2Supported)
            {
                var floatSums = X86.Avx.mm256_setzero_ps();
                var intSums = X86.Avx.mm256_setzero_si256();
                var pickKeys = X86.Avx.mm256_set1_epi32(-1);
                var heads = X86.Avx.mm256_setzero_si256();
                for (var slot = 0; slot < active; slot++)
                {
                    var lanes = X86.Avx.mm256_load_si256(rows + ((ushort)(origins[slot] + frame) << 3));
                    floatSums = X86.Avx.mm256_add_ps(floatSums, lanes);
                    intSums = X86.Avx2.mm256_add_epi32(intSums, lanes);
                    pickKeys = X86.Avx2.mm256_max_epi32(pickKeys, X86.Avx2.mm256_or_si256(lanes, X86.Avx.mm256_set1_epi32(slot)));
                    heads = X86.Avx2.mm256_or_si256(heads, lanes);
                }
                lift = floatSums.Float3;
                pickKey = pickKeys.SInt2;
                any = heads.UInt0;
                damage = intSums.SInt4;
                stat = intSums.SInt5;
            }
            else
            {
                lift = 0f;
                pickKey = -1;
                any = 0u;
                damage = 0;
                stat = 0;
                for (var slot = 0; slot < active; slot++)
                {
                    var row = rows + ((ushort)(origins[slot] + frame) << 3);
                    lift += ((float*)row)[3];
                    pickKey = math.max(pickKey, row[2] | slot);
                    any |= (uint)row[0];
                    damage += row[4];
                    stat += row[5];
                }
            }
            var speed = pickKey >= 0 ? ((float*)(rows + ((ushort)(origins[pickKey & 255] + frame) << 3)))[1] : 0f;
            motion.X += speed * stats.Walk;
            motion.Y += lift * stats.Jump;
            vitals.Health -= damage;
            vitals.Stamina += stat;
            return any;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint MoveScalar(int* rows, ushort* origins, int active, uint frame, ref Motion motion, in Stats stats)
        {
            var lift = 0f;
            var speed = 0f;
            var best = 0;
            var has = false;
            var any = 0u;
            for (var slot = 0; slot < active; slot++)
            {
                var row = rows + ((ushort)(origins[slot] + frame) << 3);
                var head = row[0];
                any |= (uint)head;
                lift += ((float*)row)[3];
                if ((head & 1) != 0)
                {
                    var priority = row[2] >> 8;
                    if (!has || priority >= best)
                    {
                        best = priority;
                        speed = ((float*)row)[1];
                        has = true;
                    }
                }
            }
            motion.X += speed * stats.Walk;
            motion.Y += lift * stats.Jump;
            return any;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Dues(in CostPlan plan, ushort* origins, ushort* dues, int active, uint frame, ref ushort next, CostLane* damageLane, CostLane* statLane, in ArchetypeChunk chunk, Entity entity, int row)
        {
            var at = (ushort)frame;
            var least = (int)ushort.MaxValue;
            for (var slot = 0; slot < active; slot++)
            {
                if (dues[slot] == at)
                {
                    var r = (ushort)(origins[slot] + frame);
                    var lanes = plan.Rows + (r << 3);
                    var damage = lanes[4];
                    var stat = lanes[5];
                    if (damageLane == null)
                    {
                    }
                    else if (statLane == null)
                    {
                        if ((damage | stat) != 0) damageLane->Push(in chunk, entity, row, damage << 16 | (stat & 0xFFFF));
                    }
                    else
                    {
                        if (damage != 0) damageLane->Push(in chunk, entity, row, damage << 16);
                        if (stat != 0) statLane->Push(in chunk, entity, row, stat & 0xFFFF);
                    }
                    origins[slot] = (ushort)(origins[slot] + plan.Shift[r]);
                    dues[slot] = (ushort)(at + plan.Gap[r]);
                }
                var ahead = (ushort)(dues[slot] - at);
                if (ahead < least) least = ahead;
            }
            next = (ushort)(at + least);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostFusedJob : IJobChunk
    {
        public CostPlan Plan;
        public uint Frame;
        public int Fixed;
        public bool Dues;
        public bool ScalarPick;
        [NativeDisableUnsafePtrRestriction] public CostLane* Lanes;
        public int Threads;
        [NativeSetThreadIndex] public int ThreadIndex;
        public DynamicComponentTypeHandle OriginsHandle;
        public BufferTypeHandle<CostSpillDue> SpillHandle;
        public ComponentTypeHandle<CostDues> DuesHandle;
        public ComponentTypeHandle<CostDense> DenseHandle;
        [ReadOnly] public EntityTypeHandle EntityHandle;
        public ComponentTypeHandle<Motion> MotionHandle;
        [ReadOnly] public ComponentTypeHandle<Stats> StatsHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var motion = (Motion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle);
            var stats = (Stats*)chunk.GetRequiredComponentDataPtrRO(ref StatsHandle);
            var count = chunk.Count;
            var fixedOrigins = Fixed > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref OriginsHandle, Fixed * sizeof(ushort)).GetUnsafePtr() : null;
            var buffers = Fixed > 0 ? default : chunk.GetUntypedBufferAccessor(ref OriginsHandle);
            CostDense* dense = null;
            ushort* fixedDues = null;
            var spills = default(BufferAccessor<CostSpillDue>);
            Entity* entities = null;
            var lane = Lanes + ThreadIndex;
            if (Dues)
            {
                dense = (CostDense*)chunk.GetRequiredComponentDataPtrRW(ref DenseHandle);
                if (Fixed > 0) fixedDues = (ushort*)chunk.GetRequiredComponentDataPtrRW(ref DuesHandle);
                else spills = chunk.GetBufferAccessor(ref SpillHandle);
                entities = (Entity*)chunk.GetNativeArray(EntityHandle).GetUnsafeReadOnlyPtr();
            }
            for (var row = 0; row < count; row++)
            {
                ushort* origins;
                int active;
                if (Fixed > 0)
                {
                    origins = fixedOrigins + row * Fixed;
                    active = Fixed;
                }
                else origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out active);
                if (active == 2) CostKernels.Move(Plan.Rows, origins, 2, Frame, ref motion[row], in stats[row]);
                else if (active == 4) CostKernels.Move(Plan.Rows, origins, 4, Frame, ref motion[row], in stats[row]);
                else CostKernels.Move(Plan.Rows, origins, active, Frame, ref motion[row], in stats[row]);
                if (!Dues || dense[row].Next != (ushort)Frame) continue;
                var dues = Fixed > 0 ? fixedDues + row * 4 : (ushort*)spills[row].GetUnsafePtr();
                CostKernels.Dues(in Plan, origins, dues, active, Frame, ref dense[row].Next, lane, null, in chunk, entities[row], row);
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostMoveJob : IJobChunk
    {
        public CostPlan Plan;
        public uint Frame;
        public int Fixed;
        public bool Checks;
        public bool ScalarPick;
        public DynamicComponentTypeHandle OriginsHandle;
        [ReadOnly] public ComponentTypeHandle<CostDense> DenseHandle;
        [ReadOnly] public ComponentTypeHandle<CostPlaying> PlayingHandle;
        public ComponentTypeHandle<CostActiveA> ActiveAHandle;
        [ReadOnly] public ComponentTypeHandle<CostEnabledA> EnabledAHandle;
        public ComponentTypeHandle<CostActiveB> ActiveBHandle;
        [ReadOnly] public ComponentTypeHandle<CostEnabledB> EnabledBHandle;
        public ComponentTypeHandle<Motion> MotionHandle;
        [ReadOnly] public ComponentTypeHandle<Stats> StatsHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var count = chunk.Count;
            var all0 = count >= 64 ? ~0ul : (1ul << count) - 1;
            var all1 = count <= 64 ? 0ul : count >= 128 ? ~0ul : (1ul << (count - 64)) - 1;
            v128 activeA = default, activeB = default;
            CostDense* dense = null;
            if (Checks)
            {
                var playing = chunk.GetEnableableBits(ref PlayingHandle);
                var denseBits = chunk.GetEnableableBits(ref DenseHandle);
                activeA = chunk.GetEnableableBits(ref ActiveAHandle);
                var enabledA = chunk.GetEnableableBits(ref EnabledAHandle);
                activeB = chunk.GetEnableableBits(ref ActiveBHandle);
                var enabledB = chunk.GetEnableableBits(ref EnabledBHandle);
                var rows0 = (activeA.ULong0 | playing.ULong0) & enabledA.ULong0 & (activeB.ULong0 | playing.ULong0) & enabledB.ULong0 & denseBits.ULong0;
                var rows1 = (activeA.ULong1 | playing.ULong1) & enabledA.ULong1 & (activeB.ULong1 | playing.ULong1) & enabledB.ULong1 & denseBits.ULong1;
                if ((rows0 & all0) != all0 || (rows1 & all1) != all1) return;
                dense = (CostDense*)chunk.GetRequiredComponentDataPtrRO(ref DenseHandle);
            }
            var motion = (Motion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle);
            var stats = (Stats*)chunk.GetRequiredComponentDataPtrRO(ref StatsHandle);
            var fixedOrigins = Fixed > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref OriginsHandle, Fixed * sizeof(ushort)).GetUnsafePtr() : null;
            var buffers = Fixed > 0 ? default : chunk.GetUntypedBufferAccessor(ref OriginsHandle);
            ulong listedA0 = 0, listedA1 = 0, listedB0 = 0, listedB1 = 0;
            for (var row = 0; row < count; row++)
            {
                ushort* origins;
                int active;
                if (Fixed > 0)
                {
                    origins = fixedOrigins + row * Fixed;
                    active = Checks ? dense[row].Count : Fixed;
                }
                else origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out active);
                uint any;
                if (ScalarPick)
                {
                    if (active == 2) any = CostKernels.MoveScalar(Plan.Rows, origins, 2, Frame, ref motion[row], in stats[row]);
                    else if (active == 4) any = CostKernels.MoveScalar(Plan.Rows, origins, 4, Frame, ref motion[row], in stats[row]);
                    else any = CostKernels.MoveScalar(Plan.Rows, origins, active, Frame, ref motion[row], in stats[row]);
                }
                else
                {
                    if (active == 2) any = CostKernels.Move(Plan.Rows, origins, 2, Frame, ref motion[row], in stats[row]);
                    else if (active == 4) any = CostKernels.Move(Plan.Rows, origins, 4, Frame, ref motion[row], in stats[row]);
                    else any = CostKernels.Move(Plan.Rows, origins, active, Frame, ref motion[row], in stats[row]);
                }
                if (!Checks) continue;
                var bit = 1ul << (row & 63);
                if ((any & 1) != 0) { if (row < 64) listedA0 |= bit; else listedA1 |= bit; }
                if ((any & 2) != 0) { if (row < 64) listedB0 |= bit; else listedB1 |= bit; }
            }
            if (!Checks) return;
            Diff(in chunk, ref ActiveAHandle, activeA, listedA0, listedA1, all0, all1);
            Diff(in chunk, ref ActiveBHandle, activeB, listedB0, listedB1, all0, all1);
        }

        internal static void Diff<T>(in ArchetypeChunk chunk, ref ComponentTypeHandle<T> handle, v128 before, ulong now0, ulong now1, ulong all0, ulong all1) where T : unmanaged, IComponentData, IEnableableComponent
        {
            var d0 = (now0 ^ before.ULong0) & all0;
            var d1 = (now1 ^ before.ULong1) & all1;
            if ((d0 | d1) == 0) return;
            var mask = chunk.GetEnabledMask(ref handle);
            for (; d0 != 0; d0 &= d0 - 1) { var b = math.tzcnt(d0); mask[b] = (now0 >> b & 1) != 0; }
            for (; d1 != 0; d1 &= d1 - 1) { var b = math.tzcnt(d1); mask[64 + b] = (now1 >> b & 1) != 0; }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostComposedJob : IJobChunk
    {
        public CostPlan Plan;
        public uint Frame;
        public int Fixed;
        [ReadOnly] public DynamicComponentTypeHandle OriginsHandle;
        [ReadOnly] public ComponentTypeHandle<CostDense> DenseHandle;
        [ReadOnly] public ComponentTypeHandle<CostPlaying> PlayingHandle;
        public ComponentTypeHandle<CostActiveA> ActiveAHandle;
        [ReadOnly] public ComponentTypeHandle<CostEnabledA> EnabledAHandle;
        public ComponentTypeHandle<CostActiveB> ActiveBHandle;
        [ReadOnly] public ComponentTypeHandle<CostEnabledB> EnabledBHandle;
        public ComponentTypeHandle<Motion> MotionHandle;
        [ReadOnly] public ComponentTypeHandle<Stats> StatsHandle;
        public ComponentTypeHandle<Vitals> VitalsHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var count = chunk.Count;
            var all0 = count >= 64 ? ~0ul : (1ul << count) - 1;
            var all1 = count <= 64 ? 0ul : count >= 128 ? ~0ul : (1ul << (count - 64)) - 1;
            var playing = chunk.GetEnableableBits(ref PlayingHandle);
            var denseBits = chunk.GetEnableableBits(ref DenseHandle);
            var activeA = chunk.GetEnableableBits(ref ActiveAHandle);
            var enabledA = chunk.GetEnableableBits(ref EnabledAHandle);
            var activeB = chunk.GetEnableableBits(ref ActiveBHandle);
            var enabledB = chunk.GetEnableableBits(ref EnabledBHandle);
            var rows0 = (activeA.ULong0 | playing.ULong0) & enabledA.ULong0 & (activeB.ULong0 | playing.ULong0) & enabledB.ULong0 & denseBits.ULong0;
            var rows1 = (activeA.ULong1 | playing.ULong1) & enabledA.ULong1 & (activeB.ULong1 | playing.ULong1) & enabledB.ULong1 & denseBits.ULong1;
            if ((rows0 & all0) != all0 || (rows1 & all1) != all1) return;
            var dense = (CostDense*)chunk.GetRequiredComponentDataPtrRO(ref DenseHandle);
            var motion = (Motion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle);
            var stats = (Stats*)chunk.GetRequiredComponentDataPtrRO(ref StatsHandle);
            var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRW(ref VitalsHandle);
            var fixedOrigins = Fixed > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref OriginsHandle, Fixed * sizeof(ushort)).GetUnsafeReadOnlyPtr() : null;
            var buffers = Fixed > 0 ? default : chunk.GetUntypedBufferAccessor(ref OriginsHandle);
            ulong listedA0 = 0, listedA1 = 0, listedB0 = 0, listedB1 = 0;
            for (var row = 0; row < count; row++)
            {
                ushort* origins;
                int active;
                if (Fixed > 0)
                {
                    origins = fixedOrigins + row * Fixed;
                    active = dense[row].Count;
                }
                else origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out active);
                uint any;
                if (active == 2) any = CostKernels.MoveLanes(Plan.Rows, origins, 2, Frame, ref motion[row], in stats[row], ref vitals[row]);
                else if (active == 4) any = CostKernels.MoveLanes(Plan.Rows, origins, 4, Frame, ref motion[row], in stats[row], ref vitals[row]);
                else any = CostKernels.MoveLanes(Plan.Rows, origins, active, Frame, ref motion[row], in stats[row], ref vitals[row]);
                var bit = 1ul << (row & 63);
                if ((any & 1) != 0) { if (row < 64) listedA0 |= bit; else listedA1 |= bit; }
                if ((any & 2) != 0) { if (row < 64) listedB0 |= bit; else listedB1 |= bit; }
            }
            CostMoveJob.Diff(in chunk, ref ActiveAHandle, activeA, listedA0, listedA1, all0, all1);
            CostMoveJob.Diff(in chunk, ref ActiveBHandle, activeB, listedB0, listedB1, all0, all1);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostTickJob : IJobChunk
    {
        public CostPlan Plan;
        public uint Frame;
        public int Fixed;
        public bool Checks;
        public bool TwoKinds;
        [NativeDisableUnsafePtrRestriction] public CostLane* Lanes;
        public int Threads;
        [NativeSetThreadIndex] public int ThreadIndex;
        public DynamicComponentTypeHandle OriginsHandle;
        public BufferTypeHandle<CostSpillDue> SpillHandle;
        public ComponentTypeHandle<CostDues> DuesHandle;
        public ComponentTypeHandle<CostDense> DenseHandle;
        [ReadOnly] public ComponentTypeHandle<CostPlaying> PlayingHandle;
        [ReadOnly] public EntityTypeHandle EntityHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var count = chunk.Count;
            var walk0 = count >= 64 ? ~0ul : (1ul << count) - 1;
            var walk1 = count <= 64 ? 0ul : count >= 128 ? ~0ul : (1ul << (count - 64)) - 1;
            if (Checks)
            {
                var playing = chunk.GetEnableableBits(ref PlayingHandle);
                var denseBits = chunk.GetEnableableBits(ref DenseHandle);
                walk0 &= playing.ULong0 & denseBits.ULong0;
                walk1 &= playing.ULong1 & denseBits.ULong1;
            }
            var dense = (CostDense*)chunk.GetRequiredComponentDataPtrRW(ref DenseHandle);
            var fixedOrigins = Fixed > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref OriginsHandle, Fixed * sizeof(ushort)).GetUnsafePtr() : null;
            var buffers = Fixed > 0 ? default : chunk.GetUntypedBufferAccessor(ref OriginsHandle);
            var fixedDues = Fixed > 0 ? (ushort*)chunk.GetRequiredComponentDataPtrRW(ref DuesHandle) : null;
            var spills = Fixed > 0 ? default : chunk.GetBufferAccessor(ref SpillHandle);
            var entities = (Entity*)chunk.GetNativeArray(EntityHandle).GetUnsafeReadOnlyPtr();
            var damageLane = Lanes == null ? null : Lanes + ThreadIndex;
            var statLane = TwoKinds && Lanes != null ? Lanes + Threads + ThreadIndex : null;
            var at = (ushort)Frame;
            for (var word = 0; word < 2; word++)
                for (var bits = word == 0 ? walk0 : walk1; bits != 0; bits &= bits - 1)
                {
                    var row = (word << 6) + math.tzcnt(bits);
                    if (dense[row].Next != at) continue;
                    ushort* origins;
                    int active;
                    if (Fixed > 0)
                    {
                        origins = fixedOrigins + row * Fixed;
                        active = Fixed;
                    }
                    else origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out active);
                    var dues = Fixed > 0 ? fixedDues + row * 4 : (ushort*)spills[row].GetUnsafePtr();
                    CostKernels.Dues(in Plan, origins, dues, active, Frame, ref dense[row].Next, damageLane, statLane, in chunk, entities[row], row);
                }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostVitalsWalkJob : IJobChunk
    {
        public bool Checks;
        [ReadOnly] public ComponentTypeHandle<CostActiveA> ActiveHandle;
        [ReadOnly] public ComponentTypeHandle<CostEnabledA> EnabledHandle;
        [NativeDisableUnsafePtrRestriction] public int* Seen;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!Checks) return;
            var active = chunk.GetEnableableBits(ref ActiveHandle);
            var enabled = chunk.GetEnableableBits(ref EnabledHandle);
            if (((active.ULong0 & ~enabled.ULong0) | (active.ULong1 & ~enabled.ULong1)) != 0) *Seen = 1;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostVitalsLaneJob : IJobChunk
    {
        public CostPlan Plan;
        public uint Frame;
        public int Fixed;
        [ReadOnly] public DynamicComponentTypeHandle OriginsHandle;
        [ReadOnly] public ComponentTypeHandle<CostDense> DenseHandle;
        [ReadOnly] public ComponentTypeHandle<CostActiveA> ActiveHandle;
        [ReadOnly] public ComponentTypeHandle<CostEnabledA> EnabledHandle;
        public ComponentTypeHandle<Vitals> VitalsHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var count = chunk.Count;
            var active0 = chunk.GetEnableableBits(ref ActiveHandle);
            var enabled0 = chunk.GetEnableableBits(ref EnabledHandle);
            var denseBits = chunk.GetEnableableBits(ref DenseHandle);
            if (((active0.ULong0 | enabled0.ULong0 | denseBits.ULong0) | (active0.ULong1 | enabled0.ULong1 | denseBits.ULong1)) == 0) return;
            var dense = (CostDense*)chunk.GetRequiredComponentDataPtrRO(ref DenseHandle);
            var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRW(ref VitalsHandle);
            var fixedOrigins = Fixed > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref OriginsHandle, Fixed * sizeof(ushort)).GetUnsafeReadOnlyPtr() : null;
            var buffers = Fixed > 0 ? default : chunk.GetUntypedBufferAccessor(ref OriginsHandle);
            var rows = Plan.Rows;
            for (var row = 0; row < count; row++)
            {
                ushort* origins;
                int active;
                if (Fixed > 0)
                {
                    origins = fixedOrigins + row * Fixed;
                    active = dense[row].Count;
                }
                else origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out active);
                var damage = 0;
                var stat = 0;
                for (var slot = 0; slot < active; slot++)
                {
                    var lanes = rows + ((ushort)(origins[slot] + Frame) << 3);
                    damage += lanes[4];
                    stat += lanes[5];
                }
                vitals[row].Health -= damage;
                vitals[row].Stamina += stat;
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct CostReplayJob : IJobParallelFor
    {
        [NativeDisableUnsafePtrRestriction] public CostLane* Lanes;
        [NativeDisableContainerSafetyRestriction] public ComponentTypeHandle<Vitals> VitalsHandle;

        public void Execute(int index)
        {
            var lane = Lanes + index;
            var n = lane->Count;
            if (n == 0) return;
            var items = lane->Items;
            var last = default(ArchetypeChunk);
            var valid = false;
            Vitals* vitals = null;
            for (var i = 0; i < n; i++)
            {
                if (!valid || !items[i].Chunk.Equals(last))
                {
                    last = items[i].Chunk;
                    valid = true;
                    vitals = (Vitals*)last.GetRequiredComponentDataPtrRW(ref VitalsHandle);
                }
                var value = items[i].Value;
                ref var v = ref vitals[items[i].Row];
                v.Health -= value >> 16;
                v.Stamina += (short)(value & 0xFFFF);
            }
            lane->Count = 0;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct CostClearJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public CostLane* Lanes;
        public int Count;
        [NativeDisableUnsafePtrRestriction] public int* Pending;

        public void Execute()
        {
            var pending = 0;
            for (var i = 0; i < Count; i++) pending += Lanes[i].Count;
            *Pending = pending;
        }
    }

    public sealed unsafe class CostWorld : IDisposable
    {
        const ulong OriginSeed = 0x1234567ul;
        const uint SeedFrame = 1000;
        readonly World _world;
        readonly EntityManager _entities;
        readonly EntityQuery _characters;
        readonly CostLevel _level;
        readonly CostPlan _plan;
        readonly OriginTable _table;
        readonly int _fixed;
        readonly ComponentType _originType;
        readonly CostLane* _lanes;
        readonly int _threads;
        readonly int* _scratch;
        uint _frame;
        public readonly int ChunkCapacity;
        public readonly int PadBytes;
        readonly CostPlan _ends;

        public CostWorld(OriginTable table, CostPlan plan, CostPlan ends, int characters, int active, CostLevel level, int targetCapacity)
        {
            _world = new World("OriginCosts");
            _entities = _world.EntityManager;
            _level = level;
            _ends = ends;
            _plan = level >= CostLevel.LanePulses ? ends : plan;
            _table = table;
            _fixed = active == 2 || active == 4 ? active : 0;
            _originType = _fixed == 2 ? ComponentType.ReadWrite<Fixed2Origins>() : _fixed == 4 ? ComponentType.ReadWrite<Fixed4Origins>() : ComponentType.ReadWrite<HeapOrigin>();
            var types = new System.Collections.Generic.List<ComponentType> { ComponentType.ReadWrite<Motion>(), ComponentType.ReadWrite<Vitals>(), ComponentType.ReadWrite<Stats>(), _originType };
            if (level >= CostLevel.Dues)
            {
                types.Add(ComponentType.ReadWrite<CostDense>());
                types.Add(_fixed > 0 ? ComponentType.ReadWrite<CostDues>() : ComponentType.ReadWrite<CostSpillDue>());
            }
            if (level >= CostLevel.Checks)
            {
                types.Add(ComponentType.ReadWrite<CostPlaying>());
                types.Add(ComponentType.ReadWrite<CostActiveA>());
                types.Add(ComponentType.ReadWrite<CostEnabledA>());
                types.Add(ComponentType.ReadWrite<CostActiveB>());
                types.Add(ComponentType.ReadWrite<CostEnabledB>());
            }
            if (level >= CostLevel.Chunk) PadBytes = AddPads(types, targetCapacity);
            var archetype = _entities.CreateArchetype(types.ToArray());
            ChunkCapacity = archetype.ChunkCapacity;
            _characters = _entities.CreateEntityQuery(ComponentType.ReadWrite<Motion>(), ComponentType.ReadWrite<Vitals>(), ComponentType.ReadOnly<Stats>(), _originType);
            _threads = Math.Max(1, JobsUtility.ThreadIndexCount);
            _lanes = (CostLane*)UnsafeUtility.Malloc((long)2 * _threads * sizeof(CostLane), 64, Allocator.Persistent);
            UnsafeUtility.MemClear(_lanes, (long)2 * _threads * sizeof(CostLane));
            _scratch = (int*)UnsafeUtility.Malloc(64, 64, Allocator.Persistent);
            UnsafeUtility.MemClear(_scratch, 64);
            using (var created = _entities.CreateEntity(archetype, characters, Allocator.Persistent))
                Seed(table, created, active);
        }

        int AddPads(System.Collections.Generic.List<ComponentType> types, int targetCapacity)
        {
            var pads = new[] { ComponentType.ReadWrite<Pad2>(), ComponentType.ReadWrite<Pad4>(), ComponentType.ReadWrite<Pad8>(), ComponentType.ReadWrite<Pad16>(), ComponentType.ReadWrite<Pad32>(), ComponentType.ReadWrite<Pad64>(), ComponentType.ReadWrite<Pad128>() };
            for (var units = 0; units < 128; units++)
            {
                var trial = new System.Collections.Generic.List<ComponentType>(types);
                for (var p = 0; p < pads.Length; p++)
                    if ((units >> p & 1) != 0) trial.Add(pads[p]);
                if (_entities.CreateArchetype(trial.ToArray()).ChunkCapacity <= targetCapacity)
                {
                    types.Clear();
                    types.AddRange(trial);
                    return units * 2;
                }
            }
            throw new InvalidOperationException("no padding reaches chunk capacity " + targetCapacity);
        }

        void Seed(OriginTable table, NativeArray<Entity> created, int active)
        {
            _frame = SeedFrame;
            var random = new XorShift64(OriginSeed);
            var entityHandle = _entities.GetEntityTypeHandle();
            var originHandle = _entities.GetDynamicComponentTypeHandle(_originType);
            var motionHandle = _entities.GetComponentTypeHandle<Motion>(false);
            var vitalsHandle = _entities.GetComponentTypeHandle<Vitals>(false);
            var statsHandle = _entities.GetComponentTypeHandle<Stats>(false);
            var denseHandle = _entities.GetComponentTypeHandle<CostDense>(false);
            var duesHandle = _entities.GetComponentTypeHandle<CostDues>(false);
            var spillHandle = _entities.GetBufferTypeHandle<CostSpillDue>(false);
            using var chunks = _characters.ToArchetypeChunkArray(Allocator.Persistent);
            var character = 0;
            var first = (ushort)(SeedFrame + 1);
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var ids = chunk.GetEntityDataPtrRO(entityHandle);
                var motion = (Motion*)chunk.GetRequiredComponentDataPtrRW(ref motionHandle);
                var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRW(ref vitalsHandle);
                var stats = (Stats*)chunk.GetRequiredComponentDataPtrRW(ref statsHandle);
                var fixedOrigins = _fixed > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref originHandle, _fixed * sizeof(ushort)).GetUnsafePtr() : null;
                var buffers = _fixed > 0 ? default : chunk.GetUntypedBufferAccessor(ref originHandle);
                var dues = _level >= CostLevel.Dues && _fixed > 0 ? (ushort*)chunk.GetRequiredComponentDataPtrRW(ref duesHandle) : null;
                var spills = _level >= CostLevel.Dues && _fixed == 0 ? chunk.GetBufferAccessor(ref spillHandle) : default;
                var dense = _level >= CostLevel.Dues ? (CostDense*)chunk.GetRequiredComponentDataPtrRW(ref denseHandle) : null;
                for (var row = 0; row < chunk.Count; row++, character++)
                {
                    if (ids[row] != created[character]) throw new InvalidOperationException("chunk order differs from creation order at character " + character);
                    motion[row] = default;
                    vitals[row] = new Vitals { Health = 1000000, Stamina = 0 };
                    stats[row] = new Stats { Walk = 1f + character % 7 * 0.5f, Jump = 2f + character % 3 };
                    ushort* origins;
                    if (_fixed > 0) origins = fixedOrigins + row * _fixed;
                    else
                    {
                        buffers.ResizeUninitialized(row, active);
                        origins = (ushort*)buffers.GetUnsafePtr(row);
                    }
                    ushort* rowDues = null;
                    if (dues != null) rowDues = dues + row * 4;
                    else if (_level >= CostLevel.Dues)
                    {
                        spills[row].ResizeUninitialized(active);
                        rowDues = (ushort*)spills[row].GetUnsafePtr();
                    }
                    var least = (int)ushort.MaxValue;
                    for (var slot = 0; slot < active; slot++)
                    {
                        var timeline = (int)(random.Next() % OriginTable.Timelines);
                        var tick = random.Next() % (uint)(table.DurationOfTimeline[timeline] - 1);
                        origins[slot] = (ushort)(table.FirstRowOfTimeline[timeline] + tick - _frame);
                        if (rowDues == null) continue;
                        var ahead = _plan.Ahead[(ushort)(origins[slot] + first)];
                        rowDues[slot] = (ushort)(first + ahead);
                        if (ahead < least) least = ahead;
                    }
                    if (dense != null) dense[row] = new CostDense { Next = (ushort)(first + least), Count = (byte)active };
                }
            }
        }

        public void Step()
        {
            _frame++;
            var originsHandle = _entities.GetDynamicComponentTypeHandle(_originType);
            var motionHandle = _entities.GetComponentTypeHandle<Motion>(false);
            var statsHandle = _entities.GetComponentTypeHandle<Stats>(true);
            var vitalsHandle = _entities.GetComponentTypeHandle<Vitals>(false);
            var denseHandle = _entities.GetComponentTypeHandle<CostDense>(false);
            var duesHandle = _entities.GetComponentTypeHandle<CostDues>(false);
            var spillHandle = _entities.GetBufferTypeHandle<CostSpillDue>(false);
            var entityHandle = _entities.GetEntityTypeHandle();
            var checks = _level >= CostLevel.Checks;
            var twoKinds = _level >= CostLevel.TwoKinds;
            JobHandle done;
            if (_level <= CostLevel.Chunk)
            {
                done = new OriginChunkJob { Table = _table, Frame = _frame, FixedActive = _fixed, ReadOnlyFloor = false, UnrollSmallCounts = true, OriginsHandle = originsHandle, MotionHandle = motionHandle, VitalsHandle = vitalsHandle, StatsHandle = statsHandle }.ScheduleParallel(_characters, default);
            }
            else if (_level == CostLevel.Dues)
            {
                done = new CostFusedJob { Plan = _plan, Frame = _frame, Fixed = _fixed, Dues = true, Lanes = _lanes, Threads = _threads, OriginsHandle = originsHandle, SpillHandle = spillHandle, DuesHandle = duesHandle, DenseHandle = denseHandle, EntityHandle = entityHandle, MotionHandle = motionHandle, StatsHandle = statsHandle }.ScheduleParallel(_characters, default);
                done = new CostReplayJob { Lanes = _lanes, VitalsHandle = vitalsHandle }.Schedule(_threads, 1, done);
            }
            else if (_level == CostLevel.LanePulses)
            {
                var move = new CostMoveJob { Plan = _plan, Frame = _frame, Fixed = _fixed, Checks = true, ScalarPick = true, OriginsHandle = originsHandle, DenseHandle = _entities.GetComponentTypeHandle<CostDense>(true), PlayingHandle = _entities.GetComponentTypeHandle<CostPlaying>(true), ActiveAHandle = _entities.GetComponentTypeHandle<CostActiveA>(false), EnabledAHandle = _entities.GetComponentTypeHandle<CostEnabledA>(true), ActiveBHandle = _entities.GetComponentTypeHandle<CostActiveB>(false), EnabledBHandle = _entities.GetComponentTypeHandle<CostEnabledB>(true), MotionHandle = motionHandle, StatsHandle = statsHandle };
                done = move.ScheduleParallel(_characters, default);
                done = new CostVitalsLaneJob { Plan = _plan, Frame = _frame, Fixed = _fixed, OriginsHandle = _entities.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(_originType.TypeIndex)), DenseHandle = _entities.GetComponentTypeHandle<CostDense>(true), ActiveHandle = _entities.GetComponentTypeHandle<CostActiveA>(true), EnabledHandle = _entities.GetComponentTypeHandle<CostEnabledA>(true), VitalsHandle = vitalsHandle }.ScheduleParallel(_characters, done);
                done = new CostTickJob { Plan = _plan, Frame = _frame, Fixed = _fixed, Checks = true, TwoKinds = false, Lanes = null, Threads = _threads, OriginsHandle = originsHandle, SpillHandle = spillHandle, DuesHandle = duesHandle, DenseHandle = denseHandle, PlayingHandle = _entities.GetComponentTypeHandle<CostPlaying>(true), EntityHandle = entityHandle }.ScheduleParallel(_characters, done);
            }
            else if (_level >= CostLevel.Composite)
            {
                done = new CostComposedJob { Plan = _plan, Frame = _frame, Fixed = _fixed, OriginsHandle = _entities.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(_originType.TypeIndex)), DenseHandle = _entities.GetComponentTypeHandle<CostDense>(true), PlayingHandle = _entities.GetComponentTypeHandle<CostPlaying>(true), ActiveAHandle = _entities.GetComponentTypeHandle<CostActiveA>(false), EnabledAHandle = _entities.GetComponentTypeHandle<CostEnabledA>(true), ActiveBHandle = _entities.GetComponentTypeHandle<CostActiveB>(false), EnabledBHandle = _entities.GetComponentTypeHandle<CostEnabledB>(true), MotionHandle = motionHandle, StatsHandle = statsHandle, VitalsHandle = vitalsHandle }.ScheduleParallel(_characters, default);
                done = new CostTickJob { Plan = _plan, Frame = _frame, Fixed = _fixed, Checks = true, TwoKinds = false, Lanes = null, Threads = _threads, OriginsHandle = originsHandle, SpillHandle = spillHandle, DuesHandle = duesHandle, DenseHandle = denseHandle, PlayingHandle = _entities.GetComponentTypeHandle<CostPlaying>(true), EntityHandle = entityHandle }.ScheduleParallel(_characters, done);
            }
            else
            {
                JobHandle start = default;
                if (twoKinds) start = new CostClearJob { Lanes = _lanes, Count = 2 * _threads, Pending = _scratch }.Schedule();
                var tick = new CostTickJob { Plan = _plan, Frame = _frame, Fixed = _fixed, Checks = checks, TwoKinds = twoKinds, Lanes = _lanes, Threads = _threads, OriginsHandle = originsHandle, SpillHandle = spillHandle, DuesHandle = duesHandle, DenseHandle = denseHandle, PlayingHandle = _entities.GetComponentTypeHandle<CostPlaying>(true), EntityHandle = entityHandle };
                var move = new CostMoveJob { Plan = _plan, Frame = _frame, Fixed = _fixed, Checks = checks, ScalarPick = _level >= CostLevel.ScalarPick, OriginsHandle = originsHandle, DenseHandle = _entities.GetComponentTypeHandle<CostDense>(true), PlayingHandle = _entities.GetComponentTypeHandle<CostPlaying>(true), ActiveAHandle = _entities.GetComponentTypeHandle<CostActiveA>(false), EnabledAHandle = _entities.GetComponentTypeHandle<CostEnabledA>(true), ActiveBHandle = _entities.GetComponentTypeHandle<CostActiveB>(false), EnabledBHandle = _entities.GetComponentTypeHandle<CostEnabledB>(true), MotionHandle = motionHandle, StatsHandle = statsHandle };
                done = move.ScheduleParallel(_characters, start);
                done = tick.ScheduleParallel(_characters, done);
                if (twoKinds) done = new CostReplayJob { Lanes = _lanes + _threads, VitalsHandle = vitalsHandle }.Schedule(_threads, 1, done);
                done = new CostVitalsWalkJob { Checks = checks, ActiveHandle = _entities.GetComponentTypeHandle<CostActiveA>(true), EnabledHandle = _entities.GetComponentTypeHandle<CostEnabledA>(true), Seen = _scratch + 1 }.ScheduleParallel(_characters, done);
                done = new CostReplayJob { Lanes = _lanes, VitalsHandle = vitalsHandle }.Schedule(_threads, 1, done);
                if (twoKinds)
                {
                    done = new CostClearJob { Lanes = _lanes, Count = _threads, Pending = _scratch + 2 }.Schedule(done);
                    done = new CostClearJob { Lanes = _lanes + _threads, Count = _threads, Pending = _scratch + 3 }.Schedule(done);
                }
            }
            done.Complete();
        }

        public ulong Hash()
        {
            _entities.CompleteAllTrackedJobs();
            var motionHandle = _entities.GetComponentTypeHandle<Motion>(true);
            var vitalsHandle = _entities.GetComponentTypeHandle<Vitals>(true);
            using var chunks = _characters.ToArchetypeChunkArray(Allocator.Persistent);
            var hash = 1469598103934665603ul;
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var motion = (Motion*)chunk.GetRequiredComponentDataPtrRO(ref motionHandle);
                var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRO(ref vitalsHandle);
                for (var row = 0; row < chunk.Count; row++)
                {
                    hash = (hash ^ *(uint*)&motion[row].X) * 1099511628211ul;
                    hash = (hash ^ *(uint*)&motion[row].Y) * 1099511628211ul;
                    hash = (hash ^ (uint)vitals[row].Health) * 1099511628211ul;
                    hash = (hash ^ (uint)vitals[row].Stamina) * 1099511628211ul;
                }
            }
            return hash;
        }

        public void Dispose()
        {
            _world.Dispose();
            for (var i = 0; i < 2 * _threads; i++)
                if (_lanes[i].Items != null) UnsafeUtility.Free(_lanes[i].Items, Allocator.Persistent);
            UnsafeUtility.Free(_lanes, Allocator.Persistent);
            UnsafeUtility.Free(_scratch, Allocator.Persistent);
        }
    }

    public static unsafe class OriginCostBench
    {
        const ulong RowSeed = 0x9E3779B97F4A7C15ul;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "OriginCosts.log");
            var cells = "1000000x2,1000000x4,50000x40,50000x100";
            var levels = "U1,Chunk,Dues,Walks,Checks,ScalarPick,TwoKinds,SlotFree,LanePulses,Composite,CompositeSlotFree";
            var warmup = 40;
            var measure = 16;
            var runs = 5;
            var portCapacity = 80;
            var slotFreeCapacity = 94;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-costLog") log = args[i + 1];
                if (args[i] == "-costCells") cells = args[i + 1];
                if (args[i] == "-costLevels") levels = args[i + 1];
                if (args[i] == "-costRuns") runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-costPortCapacity") portCapacity = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-costSlotFreeCapacity") slotFreeCapacity = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            var table = new OriginTable(RowSeed);
            var plan = new CostPlan(in table, false);
            var ends = new CostPlan(in table, true);
            try
            {
                Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " workers=" + JobsUtility.JobWorkerCount + " threadIndices=" + JobsUtility.ThreadIndexCount + " warmup=" + warmup + " measure=" + measure + " runs=" + runs + " portCapacity=" + portCapacity + " slotFreeCapacity=" + slotFreeCapacity);
                foreach (var cell in cells.Split(','))
                {
                    var size = cell.Split('x');
                    var characters = int.Parse(size[0], CultureInfo.InvariantCulture);
                    var active = int.Parse(size[1], CultureInfo.InvariantCulture);
                    foreach (var name in levels.Split(','))
                    {
                        var parts = name.Split('@');
                        var level = (CostLevel)Enum.Parse(typeof(CostLevel), parts[0]);
                        var capacity = level == CostLevel.SlotFree || level == CostLevel.CompositeSlotFree ? slotFreeCapacity : portCapacity;
                        if (parts.Length > 1) capacity = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        Cell(writer, table, plan, ends, characters, active, level, capacity, warmup, measure, runs);
                    }
                }
            }
            finally
            {
                plan.Dispose();
                ends.Dispose();
                table.Dispose();
            }
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlOriginCosts: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        static void Cell(StreamWriter writer, OriginTable table, CostPlan plan, CostPlan ends, int characters, int active, CostLevel level, int capacity, int warmup, int measure, int runs)
        {
            var medians = new double[runs];
            var samples = new double[measure];
            var hash = 0ul;
            var stable = true;
            long allocated = 0;
            var chunkCapacity = 0;
            var pad = 0;
            for (var run = 0; run < runs; run++)
            {
                using var world = new CostWorld(table, plan, ends, characters, active, level, capacity);
                for (var i = 0; i < warmup; i++) world.Step();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < measure; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    world.Step();
                    samples[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                }
                var spent = GC.GetAllocatedBytesForCurrentThread() - before;
                allocated = Math.Max(allocated, spent);
                Array.Sort(samples);
                medians[run] = samples[measure / 2];
                var runHash = world.Hash();
                if (run > 0 && runHash != hash) stable = false;
                hash = runHash;
                chunkCapacity = world.ChunkCapacity;
                pad = world.PadBytes;
            }
            Array.Sort(medians);
            Row(writer, string.Format(CultureInfo.InvariantCulture, "cell characters={0} active={1} level={2} chunkCapacity={3} padBytes={4} stepMs={5:F3} minMs={6:F3} maxMs={7:F3} alloc={8} stable={9} steps={10} hash={11:x16}",
                characters, active, level, chunkCapacity, pad, medians[runs / 2], medians[0], medians[runs - 1], allocated, stable, warmup + measure, hash));
        }
    }
}
