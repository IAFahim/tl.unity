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
        EntityQuery _requesting;
        BufferTypeHandle<TimelineRequest> _requests;
        EntityTypeHandle _entities;
        NativeList<DynamicComponentTypeHandle> _entries;
        ulong _identity;
        uint _generation;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _requesting = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback, TimelineRequest>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _requesting.SetChangedVersionFilter(ComponentType.ReadOnly<TimelineRequest>());
            _requests = state.GetBufferTypeHandle<TimelineRequest>(true);
            _entities = state.GetEntityTypeHandle();
            _entries = new NativeList<DynamicComponentTypeHandle>(Allocator.Persistent);
            state.RequireForUpdate<TimelineLibrary>();
        }

        public void OnDestroy(ref SystemState state)
        {
            _entries.Dispose();
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
                _entries.Clear();
                for (var k = 0; k < index.KindCount; k++)
                    _entries.Add(state.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(index.EntryTypes[k].TypeIndex)));
            }
            if (_requesting.IsEmpty)
                return;
            _requesting.CompleteDependency();
            for (var k = 0; k < _entries.Length; k++)
            {
                var handle = _entries[k];
                handle.Update(ref state);
                _entries[k] = handle;
            }
            _requests.Update(ref state);
            _entities.Update(ref state);
            var grow = new NativeList<TlKindGrow>(Allocator.Temp);
            var chunks = _requesting.ToArchetypeChunkArray(Allocator.Temp);
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var missing = index.Bound;
                for (var bits = index.Bound; bits != 0; bits &= bits - 1)
                {
                    var k = math.tzcnt(bits);
                    var handle = _entries[k];
                    if (chunk.Has(ref handle))
                        missing &= ~(1ul << k);
                }
                if (missing == 0)
                    continue;
                var buffers = chunk.GetBufferAccessor(ref _requests);
                var entities = chunk.GetNativeArray(_entities);
                for (var row = 0; row < chunk.Count; row++)
                {
                    var requests = buffers[row];
                    var needed = 0ul;
                    for (var i = 0; i < requests.Length; i++)
                    {
                        var request = requests[i];
                        if (request.Op == global::Tl.RequestOp.Start && request.Timeline != 0 && request.Timeline <= index.TimelineCount)
                            needed |= index.Closure[request.Timeline - 1];
                    }
                    if ((needed & missing) != 0)
                        grow.Add(new TlKindGrow { Entity = entities[row], Kinds = needed & missing });
                }
            }
            var manager = state.EntityManager;
            for (var i = 0; i < grow.Length; i++)
                Add(manager, in index, grow[i].Entity, grow[i].Kinds);
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
}
