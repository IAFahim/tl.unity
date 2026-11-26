using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Tl.Unity
{
    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup), OrderFirst = true)]
    public partial struct TlRequestResolveSystem : ISystem
    {
        EntityQuery _requests;
        BufferTypeHandle<TimelineRequest> _handle;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _requests = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineRequest>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _requests.SetChangedVersionFilter(ComponentType.ReadWrite<TimelineRequest>());
            _handle = state.GetBufferTypeHandle<TimelineRequest>(false);
            state.RequireForUpdate<TlLibraryIndex>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (_requests.IsEmpty)
                return;
            _handle.Update(ref state);
            state.Dependency = new TlRequestResolveJob { Ids = SystemAPI.GetSingleton<TlLibraryIndex>().Ids, Requests = _handle }.ScheduleParallel(_requests, state.Dependency);
        }
    }

#if !TL_CHECKED
    [DisableAutoCreation]
#endif
    [UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
    public partial struct TlCheckedStartsSystem : ISystem
    {
        EntityQuery _tick;
        int _seen;

        public void OnCreate(ref SystemState state)
        {
            _tick = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineKindTick>().WithOptions(EntityQueryOptions.IncludeSystems).Build(ref state);
            state.RequireForUpdate(_tick);
        }

        public void OnUpdate(ref SystemState state)
        {
            var dropped = _tick.GetSingleton<TimelineKindTick>().DroppedStarts;
            if (dropped == _seen)
                return;
            _seen = dropped;
            throw new System.InvalidOperationException("Tl: " + dropped + " start(s) dropped so far because the timeline id is not in the library or no Unity system runs a kind the timeline feeds (TimelineKindTick.DroppedStarts). Look timelines up by name, and check that every kind's code compiles in a Unity assembly.");
        }
    }

    [BurstCompile]
    public struct TlRequestResolveJob : IJobChunk
    {
        [ReadOnly] public NativeHashMap<Hash128, ushort> Ids;
        public BufferTypeHandle<TimelineRequest> Requests;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var buffers = chunk.GetBufferAccessor(ref Requests);
            for (var row = 0; row < chunk.Count; row++)
            {
                var requests = buffers[row];
                for (var i = 0; i < requests.Length; i++)
                {
                    var request = requests[i];
                    if (request.Timeline != 0 || !request.Asset.IsValid)
                        continue;
                    if (Ids.TryGetValue(request.Asset, out var id))
                        request.Timeline = id;
                    else
                        Missing();
                    request.Asset = default;
                    requests[i] = request;
                }
            }
        }

        static void Missing()
        {
            UnityEngine.Debug.LogWarning("Tl: code asked an object to play a timeline that is not in the loaded library. Drag that timeline into Also Plays on any object (or into a clip field of one of its timelines), or load a library that has it.");
        }
    }

    public struct TlKindGrow
    {
        public Entity Entity;
        public ulong Kinds;
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateBefore(typeof(TimelineKindTickSystem))]
    public unsafe partial struct TlKindReachSystem : ISystem
    {
        EntityQuery _partial;
        BufferTypeHandle<TimelineRequest> _requests;
        EntityTypeHandle _entities;
        NativeQueue<TlKindGrow> _grow;
        ulong _identity;
        uint _generation;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _requests = state.GetBufferTypeHandle<TimelineRequest>(true);
            _entities = state.GetEntityTypeHandle();
            _grow = new NativeQueue<TlKindGrow>(Allocator.Persistent);
            state.RequireForUpdate<TimelineLibrary>();
        }

        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            _grow.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var index = library.Kinds;
            if (index.KindCount == 0 || index.GrowStart == null || index.Bound == 0)
                return;
            var identity = (ulong)index.GrowStart;
            if (identity != _identity || library.Generation != _generation)
            {
                _identity = identity;
                _generation = library.Generation;
                _partial = Partial(ref state, in index);
            }
            if (_partial.IsEmpty)
                return;
            _requests.Update(ref state);
            _entities.Update(ref state);
            new TlKindReachJob
            {
                Requests = _requests,
                Entities = _entities,
                Closure = index.Closure,
                TimelineCount = index.TimelineCount,
                Bound = index.Bound,
                Grow = _grow.AsParallelWriter(),
            }.ScheduleParallel(_partial, state.Dependency).Complete();
            if (_grow.IsEmpty())
                return;
            var manager = state.EntityManager;
            while (_grow.TryDequeue(out var grow))
                Add(manager, in index, grow.Entity, grow.Kinds);
        }

        static EntityQuery Partial(ref SystemState state, in KindIndex index)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp);
            var none = new NativeList<ComponentType>(1, Allocator.Temp);
            var first = true;
            for (var k = 0; k < index.KindCount; k++)
            {
                if ((index.Bound & (1ul << k)) == 0) continue;
                if (!first) builder.AddAdditionalQuery();
                first = false;
                none.Clear();
                none.Add(ComponentType.ReadOnly(index.EntryTypes[k].TypeIndex));
                builder.WithAll<Playback, TimelineRequest>().WithNone(ref none).WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
            }
            var query = builder.Build(ref state);
            query.SetChangedVersionFilter(ComponentType.ReadWrite<TimelineRequest>());
            none.Dispose();
            builder.Dispose();
            return query;
        }

        static void Add(EntityManager manager, in KindIndex index, Entity entity, ulong kinds)
        {
            if (!manager.Exists(entity))
                return;
            var missing = 0ul;
            for (var bits = kinds; bits != 0; bits &= bits - 1)
            {
                var k = math.tzcnt(bits);
                if (!manager.HasComponent(entity, index.EntryTypes[k]))
                    missing |= 1ul << k;
            }
            if (missing == 0)
                return;
            var types = new FixedList128Bytes<ComponentType>();
            for (var bits = missing; bits != 0; bits &= bits - 1)
                Collect(manager, entity, index.GrowTypes, index.GrowStart[math.tzcnt(bits)], index.GrowStart[math.tzcnt(bits) + 1], ref types);
            Collect(manager, entity, index.GrowTypes, index.GrowStart[index.KindCount], index.GrowStart[index.KindCount + 1], ref types);
            if (types.Length > 0)
                manager.AddComponent(entity, new ComponentTypeSet(in types));
            for (var bits = missing; bits != 0; bits &= bits - 1)
                manager.SetComponentEnabled(entity, index.ActiveTypes[math.tzcnt(bits)], false);
        }

        static void Collect(EntityManager manager, Entity entity, ComponentType* grow, int from, int to, ref FixedList128Bytes<ComponentType> types)
        {
            for (var i = from; i < to; i++)
            {
                var type = grow[i];
                if (manager.HasComponent(entity, type) || types.Contains(type))
                    continue;
                if (types.Length == types.Capacity)
                {
                    manager.AddComponent(entity, new ComponentTypeSet(in types));
                    types.Clear();
                }
                types.Add(type);
            }
        }
    }

    [BurstCompile]
    public unsafe struct TlKindReachJob : IJobChunk
    {
        [ReadOnly] public BufferTypeHandle<TimelineRequest> Requests;
        [ReadOnly] public EntityTypeHandle Entities;
        [NativeDisableUnsafePtrRestriction] public ulong* Closure;
        public int TimelineCount;
        public ulong Bound;
        public NativeQueue<TlKindGrow>.ParallelWriter Grow;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var buffers = chunk.GetBufferAccessor(ref Requests);
            var entities = chunk.GetNativeArray(Entities);
            for (var row = 0; row < chunk.Count; row++)
            {
                var requests = buffers[row];
                var needed = 0ul;
                for (var i = 0; i < requests.Length; i++)
                {
                    var request = requests[i];
                    if (request.Op == global::Tl.RequestOp.Start && request.Timeline != 0 && request.Timeline <= TimelineCount)
                        needed |= Closure[request.Timeline - 1];
                }
                needed &= Bound;
                if (needed != 0)
                    Grow.Enqueue(new TlKindGrow { Entity = entities[row], Kinds = needed });
            }
        }
    }
}
