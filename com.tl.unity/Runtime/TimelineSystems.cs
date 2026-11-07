using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    [UpdateInGroup(typeof(TlSimulationGroup))]
    public partial class TimelineSimulationGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TimelineLibrarySystem : ISystem
    {
        EntityQuery _blobLibrary;

        public void OnCreate(ref SystemState state)
        {
            _blobLibrary = state.GetEntityQuery(ComponentType.ReadOnly<TlLibraryHandle>());
            TlGeneratedBootstrap.InstallAll();
            TimelineLibraryBootstrap.LoadSource(state.EntityManager);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!_blobLibrary.IsEmptyIgnoreFilter)
                return;
            var loaded = TimelineLibraryBootstrap.LoadSource(state.EntityManager);
#if UNITY_EDITOR
            if (loaded)
                TimelineLibraryBootstrap.PollReload(state.EntityManager);
#else
            if (loaded)
                state.Enabled = false;
#endif
        }

        public void OnDestroy(ref SystemState state)
        {
            TimelineLibraryBootstrap.Forget(state.EntityManager);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    public partial struct TimelineRequestSystem : ISystem
    {
        EntityQuery _requestQuery;
        EntityQuery _closedQuery;
        int _sequence;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _requestQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineRequest>().WithAllRW<Playback>().WithAbsent<TimelinePlaying>().Build(ref state);
            _closedQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineLibrary>().WithAllRW<ClosedPlayback>().Build(ref state);
            state.RequireForUpdate<TimelineLibrary>();
        }

        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            var view = SystemAPI.GetSingleton<TimelineLibrary>().View;
            if (!view.Kinds.IsEmpty)
                return;
            var entities = _requestQuery.ToEntityArray(Allocator.Temp);
            if (entities.Length == 0)
                return;
            var manager = state.EntityManager;
            var gathered = new NativeList<RequestEntry>(entities.Length * 2, Allocator.Temp);
            for (var e = 0; e < entities.Length; e++)
            {
                var requests = manager.GetBuffer<TimelineRequest>(entities[e]);
                for (var r = 0; r < requests.Length; r++)
                {
                    gathered.Add(new RequestEntry
                    {
                        Entity = entities[e].Index,
                        Slot = requests[r].Slot,
                        Timeline = requests[r].Timeline,
                        At = requests[r].At,
                        Op = requests[r].Op,
                        Sequence = _sequence++,
                    });
                }
            }
            TimelineKernel.SortRequests((RequestEntry*)gathered.GetUnsafePtr(), gathered.Length);
            var closed = new NativeList<ClosedPlayback>(gathered.Length, Allocator.Temp);
            for (var i = 0; i < gathered.Length; i++)
            {
                var entry = gathered[i];
                var target = FindEntity(entities, entry.Entity);
                if (target == Entity.Null)
                    continue;
                var playbacks = manager.GetBuffer<Playback>(target);
                ApplyEntry(ref playbacks, entry, in view, ref closed);
                manager.GetBuffer<TimelineRequest>(target).Clear();
            }
            if (closed.Length == 0)
                return;
            var holders = _closedQuery.ToEntityArray(Allocator.Temp);
            if (holders.Length == 0)
                return;
            var sink = manager.GetBuffer<ClosedPlayback>(holders[0]);
            for (var i = 0; i < closed.Length; i++)
                sink.Add(closed[i]);
        }

        static Entity FindEntity(NativeArray<Entity> entities, int index)
        {
            for (var i = 0; i < entities.Length; i++)
                if (entities[i].Index == index)
                    return entities[i];
            return Entity.Null;
        }

        static void ApplyEntry(ref DynamicBuffer<Playback> playbacks, RequestEntry entry, in global::Tl.LibraryView view, ref NativeList<ClosedPlayback> closed)
        {
            var entity = entry.Entity;
            switch (entry.Op)
            {
                case global::Tl.RequestOp.Start:
                    TimelineKernel.ApplyStart(ref playbacks, entity, entry.Timeline, entry.At, in view, ref closed, out _);
                    break;
                case global::Tl.RequestOp.Stop:
                    TimelineKernel.ApplyStop(ref playbacks, entity, entry.Timeline, ref closed);
                    break;
                case global::Tl.RequestOp.StopChannel:
                    TimelineKernel.ApplyStopChannel(ref playbacks, entity, (byte)entry.Timeline, ref closed);
                    break;
                case global::Tl.RequestOp.Pause:
                case global::Tl.RequestOp.Resume:
                    TimelineKernel.ApplyPause(ref playbacks, entry.Timeline, entry.Op == global::Tl.RequestOp.Pause);
                    break;
                default:
                    TimelineKernel.ApplyStopAll(ref playbacks, entity, ref closed);
                    break;
            }
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineRequestSystem))]
    public partial struct TimelineAdvanceSystem : ISystem
    {
        EntityQuery _playbackQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _playbackQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Playback>().WithAbsent<TimelinePlaying>().Build(ref state);
            state.RequireForUpdate<TimelineLibrary>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var view = SystemAPI.GetSingleton<TimelineLibrary>().View;
            if (!view.Kinds.IsEmpty)
                return;
            state.Dependency = TimelineJobs.ScheduleAdvance(ref state, _playbackQuery, view, state.Dependency);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineAdvanceSystem))]
    public partial struct TimelineEventSystem : ISystem
    {
        EntityQuery _closedQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _closedQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<ClosedPlayback>().Build(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var holders = _closedQuery.ToEntityArray(Allocator.Temp);
            var manager = state.EntityManager;
            for (var i = 0; i < holders.Length; i++)
                manager.GetBuffer<ClosedPlayback>(holders[i]).Clear();
        }
    }
}
