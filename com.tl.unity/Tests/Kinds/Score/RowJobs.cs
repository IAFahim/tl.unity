using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using static Unity.Burst.Intrinsics.X86.Avx;
using static Unity.Burst.Intrinsics.X86.Avx2;

namespace KindGame
{
    public struct ScoreRow
    {
        public int LiftFixed;
        public int Damage;
        public int Stat;
        public int Spare;
        public int WalkKey;
        public int End;
        public int Successor;
        public float Walk;
    }

    [InternalBufferCapacity(100)]
    public struct RowCell : IBufferElementData
    {
        public ushort Origin;
    }

    public struct RowTotals : IComponentData
    {
        public int LiftFixed;
        public int Damage;
        public int Stat;
        public int WalkKey;
        public float Walk;
        public int Spare;
        public ulong EndedLow;
        public ulong EndedHigh;
    }

    public struct RowMotion : IComponentData
    {
        public float X;
        public float Y;
    }

    public struct RowVitals : IComponentData
    {
        public int Health;
        public int Stamina;
    }

    public struct RowStats : IComponentData
    {
        public float Walk;
        public float JumpPower;
        public Entity Target;
    }

    public struct RowAnchor : IComponentData
    {
        public float Boost;
    }

    public unsafe struct RowScore : IComponentData
    {
        public ScoreRow* Rows;
        public int RowCount;
        public uint Frame;
    }

    public static unsafe class RowMath
    {
        public const int EndBit = unchecked((int)0x80000000);
        public const int NoWalk = int.MinValue;
        public const float FixedToFloat = 1f / 65536f;

        public static ScoreRow* RowAt(ScoreRow* rows, ushort origin, uint frame) => rows + (ushort)(origin + frame);

        public const int BlockRows = 64;

        public static void AccumulateScalar(ushort* origins, int count, ScoreRow* rows, uint frame, RowTotals* totals)
        {
            var liftFixed = 0;
            var damage = 0;
            var stat = 0;
            var walkKey = NoWalk;
            ulong endedLow = 0, endedHigh = 0;
            for (var c = 0; c < count; c++)
            {
                var index = (ushort)(origins[c] + frame);
                var row = rows + index;
                liftFixed += row->LiftFixed;
                damage += row->Damage;
                stat += row->Stat;
                walkKey = math.max(walkKey, row->WalkKey);
                if ((index & (BlockRows - 1)) != BlockRows - 1) continue;
                if (c < 64) endedLow |= 1ul << c;
                else endedHigh |= 1ul << (c - 64);
            }
            Finish(rows, liftFixed, damage, stat, walkKey, endedLow, endedHigh, totals);
        }

        public static void AccumulateAvx2(ushort* origins, int count, ScoreRow* rows, uint frame, RowTotals* totals)
        {
            var sums = mm256_setzero_si256();
            var best = mm256_set1_epi32(NoWalk);
            for (var c = 0; c < count; c++)
            {
                var row = mm256_load_si256(RowAt(rows, origins[c], frame));
                sums = mm256_add_epi32(sums, row);
                best = mm256_max_epi32(best, row);
            }
            var lastOfBlock = mm256_set1_epi16(BlockRows - 1);
            var now = mm256_set1_epi16((short)frame);
            var zero = mm256_setzero_si256();
            v256 tailGroup = default;
            var tail = (ushort*)&tailGroup;
            ulong endedLow = 0, endedHigh = 0;
            for (var first = 0; first < count; first += 16)
            {
                v256 group;
                if (first + 16 <= count) group = mm256_loadu_si256(origins + first);
                else
                {
                    for (var i = 0; i < 16; i++) tail[i] = first + i < count ? origins[first + i] : (ushort)0;
                    group = mm256_loadu_si256(tail);
                }
                var ends = mm256_cmpeq_epi16(mm256_and_si256(mm256_add_epi16(group, now), lastOfBlock), lastOfBlock);
                var packed = (uint)mm256_movemask_epi8(mm256_packs_epi16(ends, zero));
                var bits = (ulong)((packed & 0xFFu) | ((packed >> 8) & 0xFF00u));
                if (first + 16 > count) bits &= (1ul << (count - first)) - 1;
                if (first < 64) endedLow |= bits << first;
                else endedHigh |= bits << (first - 64);
            }
            Finish(rows, sums.SInt0, sums.SInt1, sums.SInt2, best.SInt4, endedLow, endedHigh, totals);
        }

        static void Finish(ScoreRow* rows, int liftFixed, int damage, int stat, int walkKey, ulong endedLow, ulong endedHigh, RowTotals* totals)
        {
            totals->LiftFixed = liftFixed;
            totals->Damage = damage;
            totals->Stat = stat;
            totals->WalkKey = walkKey;
            totals->Walk = walkKey >= 0 ? rows[walkKey & 0xFFFF].Walk : 0f;
            totals->EndedLow = endedLow;
            totals->EndedHigh = endedHigh;
        }

        public static void HandOver(ushort* origins, ulong ended, int firstCell, ScoreRow* rows, uint frame)
        {
            while (ended != 0)
            {
                var c = firstCell + math.tzcnt(ended);
                ended &= ended - 1;
                var row = RowAt(rows, origins[c], frame);
                if (row->End != 0) origins[c] = (ushort)(row->Successor - (int)(frame + 1));
            }
        }

        public static void Apply(in RowTotals totals, in RowStats stats, float targetBoost, ref RowMotion motion, ref RowVitals vitals)
        {
            motion.Y += totals.LiftFixed * FixedToFloat * stats.JumpPower;
            if (totals.WalkKey >= 0) motion.X += totals.Walk * stats.Walk * targetBoost;
            vitals.Health -= totals.Damage;
            vitals.Stamina += totals.Stat;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct RowAccumulateJob : IJobChunk
    {
        [ReadOnly] public BufferTypeHandle<RowCell> CellHandle;
        public ComponentTypeHandle<RowTotals> TotalsHandle;
        [NativeDisableUnsafePtrRestriction] public ScoreRow* Rows;
        public uint Frame;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var cells = chunk.GetBufferAccessor(ref CellHandle);
            var totals = (RowTotals*)chunk.GetRequiredComponentDataPtrRW(ref TotalsHandle);
            for (var e = 0; e < chunk.Count; e++)
            {
                var buffer = cells[e];
                var origins = (ushort*)buffer.GetUnsafeReadOnlyPtr();
                if (IsAvx2Supported) RowMath.AccumulateAvx2(origins, buffer.Length, Rows, Frame, totals + e);
                else RowMath.AccumulateScalar(origins, buffer.Length, Rows, Frame, totals + e);
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct RowHandOverJob : IJobChunk
    {
        public BufferTypeHandle<RowCell> CellHandle;
        [ReadOnly] public ComponentTypeHandle<RowTotals> TotalsHandle;
        [NativeDisableUnsafePtrRestriction] public ScoreRow* Rows;
        public uint Frame;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var cells = chunk.GetBufferAccessor(ref CellHandle);
            var totals = (RowTotals*)chunk.GetRequiredComponentDataPtrRO(ref TotalsHandle);
            for (var e = 0; e < chunk.Count; e++)
            {
                if ((totals[e].EndedLow | totals[e].EndedHigh) == 0) continue;
                var origins = (ushort*)cells[e].GetUnsafePtr();
                RowMath.HandOver(origins, totals[e].EndedLow, 0, Rows, Frame);
                RowMath.HandOver(origins, totals[e].EndedHigh, 64, Rows, Frame);
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct RowApplyJob : IJobChunk
    {
        [ReadOnly] public ComponentTypeHandle<RowTotals> TotalsHandle;
        [ReadOnly] public ComponentTypeHandle<RowStats> StatsHandle;
        [ReadOnly] public ComponentLookup<RowAnchor> Anchors;
        public ComponentTypeHandle<RowMotion> MotionHandle;
        public ComponentTypeHandle<RowVitals> VitalsHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var totals = (RowTotals*)chunk.GetRequiredComponentDataPtrRO(ref TotalsHandle);
            var stats = (RowStats*)chunk.GetRequiredComponentDataPtrRO(ref StatsHandle);
            var motion = (RowMotion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle);
            var vitals = (RowVitals*)chunk.GetRequiredComponentDataPtrRW(ref VitalsHandle);
            for (var e = 0; e < chunk.Count; e++)
                RowMath.Apply(in totals[e], in stats[e], totals[e].WalkKey >= 0 ? Anchors[stats[e].Target].Boost : 0f, ref motion[e], ref vitals[e]);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct RowFusedJob : IJobChunk
    {
        public BufferTypeHandle<RowCell> CellHandle;
        [ReadOnly] public ComponentTypeHandle<RowStats> StatsHandle;
        [ReadOnly] public ComponentLookup<RowAnchor> Anchors;
        public ComponentTypeHandle<RowMotion> MotionHandle;
        public ComponentTypeHandle<RowVitals> VitalsHandle;
        [NativeDisableUnsafePtrRestriction] public ScoreRow* Rows;
        public uint Frame;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var cells = chunk.GetBufferAccessor(ref CellHandle);
            var stats = (RowStats*)chunk.GetRequiredComponentDataPtrRO(ref StatsHandle);
            var motion = (RowMotion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle);
            var vitals = (RowVitals*)chunk.GetRequiredComponentDataPtrRW(ref VitalsHandle);
            RowTotals total;
            for (var e = 0; e < chunk.Count; e++)
            {
                var buffer = cells[e];
                var origins = (ushort*)buffer.GetUnsafePtr();
                if (IsAvx2Supported) RowMath.AccumulateAvx2(origins, buffer.Length, Rows, Frame, &total);
                else RowMath.AccumulateScalar(origins, buffer.Length, Rows, Frame, &total);
                RowMath.HandOver(origins, total.EndedLow, 0, Rows, Frame);
                RowMath.HandOver(origins, total.EndedHigh, 64, Rows, Frame);
                RowMath.Apply(in total, in stats[e], total.WalkKey >= 0 ? Anchors[stats[e].Target].Boost : 0f, ref motion[e], ref vitals[e]);
            }
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct RowFusedSystem : ISystem
    {
        EntityQuery _query;
        EntityQuery _score;
        BufferTypeHandle<RowCell> _cells;
        ComponentTypeHandle<RowStats> _stats;
        ComponentLookup<RowAnchor> _anchors;
        ComponentTypeHandle<RowMotion> _motion;
        ComponentTypeHandle<RowVitals> _vitals;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAllRW<RowCell>().WithAll<RowStats>().WithAllRW<RowMotion, RowVitals>().Build(ref state);
            _score = new EntityQueryBuilder(Allocator.Temp).WithAll<RowScore>().Build(ref state);
            _cells = state.GetBufferTypeHandle<RowCell>(false);
            _stats = state.GetComponentTypeHandle<RowStats>(true);
            _anchors = state.GetComponentLookup<RowAnchor>(true);
            _motion = state.GetComponentTypeHandle<RowMotion>(false);
            _vitals = state.GetComponentTypeHandle<RowVitals>(false);
        }

        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            var score = _score.GetSingleton<RowScore>();
            _cells.Update(ref state);
            _stats.Update(ref state);
            _anchors.Update(ref state);
            _motion.Update(ref state);
            _vitals.Update(ref state);
            state.Dependency = new RowFusedJob { CellHandle = _cells, StatsHandle = _stats, Anchors = _anchors, MotionHandle = _motion, VitalsHandle = _vitals, Rows = score.Rows, Frame = score.Frame }.ScheduleParallel(_query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct RowAccumulateSystem : ISystem
    {
        EntityQuery _query;
        EntityQuery _score;
        BufferTypeHandle<RowCell> _cells;
        ComponentTypeHandle<RowTotals> _totals;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<RowCell>().WithAllRW<RowTotals>().Build(ref state);
            _score = new EntityQueryBuilder(Allocator.Temp).WithAll<RowScore>().Build(ref state);
            _cells = state.GetBufferTypeHandle<RowCell>(true);
            _totals = state.GetComponentTypeHandle<RowTotals>(false);
        }

        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            var score = _score.GetSingleton<RowScore>();
            _cells.Update(ref state);
            _totals.Update(ref state);
            state.Dependency = new RowAccumulateJob { CellHandle = _cells, TotalsHandle = _totals, Rows = score.Rows, Frame = score.Frame }.ScheduleParallel(_query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct RowHandOverSystem : ISystem
    {
        EntityQuery _query;
        EntityQuery _score;
        BufferTypeHandle<RowCell> _cells;
        ComponentTypeHandle<RowTotals> _totals;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAllRW<RowCell>().WithAll<RowTotals>().Build(ref state);
            _score = new EntityQueryBuilder(Allocator.Temp).WithAll<RowScore>().Build(ref state);
            _cells = state.GetBufferTypeHandle<RowCell>(false);
            _totals = state.GetComponentTypeHandle<RowTotals>(true);
        }

        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            var score = _score.GetSingleton<RowScore>();
            _cells.Update(ref state);
            _totals.Update(ref state);
            state.Dependency = new RowHandOverJob { CellHandle = _cells, TotalsHandle = _totals, Rows = score.Rows, Frame = score.Frame }.ScheduleParallel(_query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct RowApplySystem : ISystem
    {
        EntityQuery _query;
        ComponentTypeHandle<RowTotals> _totals;
        ComponentTypeHandle<RowStats> _stats;
        ComponentLookup<RowAnchor> _anchors;
        ComponentTypeHandle<RowMotion> _motion;
        ComponentTypeHandle<RowVitals> _vitals;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<RowTotals, RowStats>().WithAllRW<RowMotion, RowVitals>().Build(ref state);
            _totals = state.GetComponentTypeHandle<RowTotals>(true);
            _stats = state.GetComponentTypeHandle<RowStats>(true);
            _anchors = state.GetComponentLookup<RowAnchor>(true);
            _motion = state.GetComponentTypeHandle<RowMotion>(false);
            _vitals = state.GetComponentTypeHandle<RowVitals>(false);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _totals.Update(ref state);
            _stats.Update(ref state);
            _anchors.Update(ref state);
            _motion.Update(ref state);
            _vitals.Update(ref state);
            state.Dependency = new RowApplyJob { TotalsHandle = _totals, StatsHandle = _stats, Anchors = _anchors, MotionHandle = _motion, VitalsHandle = _vitals }.ScheduleParallel(_query, state.Dependency);
        }
    }
}
