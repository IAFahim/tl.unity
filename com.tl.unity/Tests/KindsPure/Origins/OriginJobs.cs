using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindPure.Origins
{
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe struct OriginChunkJob : IJobChunk
    {
        public OriginTable Table;
        public uint Frame;
        public int FixedActive;
        public bool ReadOnlyFloor;
        public DynamicComponentTypeHandle OriginsHandle;
        public ComponentTypeHandle<Motion> MotionHandle;
        public ComponentTypeHandle<Vitals> VitalsHandle;
        [ReadOnly] public ComponentTypeHandle<Stats> StatsHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var motion = (Motion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle);
            var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRW(ref VitalsHandle);
            var stats = (Stats*)chunk.GetRequiredComponentDataPtrRO(ref StatsHandle);
            if (FixedActive > 0)
            {
                var fixedOrigins = (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref OriginsHandle, FixedActive * sizeof(ushort)).GetUnsafePtr();
                if (ReadOnlyFloor)
                    for (var row = 0; row < chunk.Count; row++)
                        Table.ReadOnlyFloor(fixedOrigins + row * FixedActive, FixedActive, Frame, ref motion[row], ref vitals[row], in stats[row]);
                else
                    for (var row = 0; row < chunk.Count; row++)
                        Table.Step(fixedOrigins + row * FixedActive, FixedActive, Frame, ref motion[row], ref vitals[row], in stats[row]);
                return;
            }
            var buffers = chunk.GetUntypedBufferAccessor(ref OriginsHandle);
            if (ReadOnlyFloor)
                for (var row = 0; row < chunk.Count; row++)
                {
                    var origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out var active);
                    Table.ReadOnlyFloor(origins, active, Frame, ref motion[row], ref vitals[row], in stats[row]);
                }
            else
                for (var row = 0; row < chunk.Count; row++)
                {
                    var origins = (ushort*)buffers.GetUnsafePtrAndLength(row, out var active);
                    Table.Step(origins, active, Frame, ref motion[row], ref vitals[row], in stats[row]);
                }
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public unsafe partial struct OriginChunkSystem : ISystem
    {
        public OriginTable Table;
        public uint Frame;
        EntityQuery _query;
        int _fixedActive;
        bool _readOnlyFloor;
        DynamicComponentTypeHandle _origins;
        ComponentTypeHandle<Motion> _motion;
        ComponentTypeHandle<Vitals> _vitals;
        ComponentTypeHandle<Stats> _stats;

        public void Bind(ref SystemState state, OriginTable table, ComponentType originType, int fixedActive, bool readOnlyFloor)
        {
            Table = table;
            _fixedActive = fixedActive;
            _readOnlyFloor = readOnlyFloor;
            _query = state.GetEntityQuery(ComponentType.ReadWrite<Motion>(), ComponentType.ReadWrite<Vitals>(), ComponentType.ReadOnly<Stats>(), originType);
            _origins = state.GetDynamicComponentTypeHandle(originType);
            _motion = state.GetComponentTypeHandle<Motion>(false);
            _vitals = state.GetComponentTypeHandle<Vitals>(false);
            _stats = state.GetComponentTypeHandle<Stats>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _origins.Update(ref state);
            _motion.Update(ref state);
            _vitals.Update(ref state);
            _stats.Update(ref state);
            state.Dependency = new OriginChunkJob
            {
                Table = Table,
                Frame = Frame,
                FixedActive = _fixedActive,
                ReadOnlyFloor = _readOnlyFloor,
                OriginsHandle = _origins,
                MotionHandle = _motion,
                VitalsHandle = _vitals,
                StatsHandle = _stats
            }.ScheduleParallel(_query, state.Dependency);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct HeapEntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(DynamicBuffer<HeapOrigin> origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            Table.Step((ushort*)origins.GetUnsafePtr(), origins.Length, Frame, ref motion, ref vitals, in stats);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct Inline2EntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(DynamicBuffer<Inline2Origin> origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            Table.Step((ushort*)origins.GetUnsafePtr(), origins.Length, Frame, ref motion, ref vitals, in stats);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct Inline4EntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(DynamicBuffer<Inline4Origin> origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            Table.Step((ushort*)origins.GetUnsafePtr(), origins.Length, Frame, ref motion, ref vitals, in stats);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct Inline8EntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(DynamicBuffer<Inline8Origin> origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            Table.Step((ushort*)origins.GetUnsafePtr(), origins.Length, Frame, ref motion, ref vitals, in stats);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct Inline100EntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(DynamicBuffer<Inline100Origin> origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            Table.Step((ushort*)origins.GetUnsafePtr(), origins.Length, Frame, ref motion, ref vitals, in stats);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct Fixed2EntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(ref Fixed2Origins origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            fixed (ushort* slots = origins.Origin) Table.Step(slots, 2, Frame, ref motion, ref vitals, in stats);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public unsafe partial struct Fixed4EntityJob : IJobEntity
    {
        public OriginTable Table;
        public uint Frame;

        void Execute(ref Fixed4Origins origins, ref Motion motion, ref Vitals vitals, in Stats stats)
        {
            fixed (ushort* slots = origins.Origin) Table.Step(slots, 4, Frame, ref motion, ref vitals, in stats);
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct OriginEntitySystem : ISystem
    {
        public OriginTable Table;
        public uint Frame;
        public OriginLayout Layout;

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            switch (Layout)
            {
                case OriginLayout.Heap:
                    state.Dependency = new HeapEntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
                case OriginLayout.Inline2:
                    state.Dependency = new Inline2EntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
                case OriginLayout.Inline4:
                    state.Dependency = new Inline4EntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
                case OriginLayout.Inline8:
                    state.Dependency = new Inline8EntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
                case OriginLayout.Inline100:
                    state.Dependency = new Inline100EntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
                case OriginLayout.Fixed2:
                    state.Dependency = new Fixed2EntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
                case OriginLayout.Fixed4:
                    state.Dependency = new Fixed4EntityJob { Table = Table, Frame = Frame }.ScheduleParallel(state.Dependency);
                    break;
            }
        }
    }
}
