using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class TimelineSimulationGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class TimelineLibrarySystem : SystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            TlGeneratedBootstrap.InstallAll();
        }

        protected override void OnUpdate()
        {
#if UNITY_EDITOR
            if (TimelineLibraryBootstrap.TryPollReload())
                TimelineLibraryBootstrap.Publish(EntityManager);
#endif
        }
    }

    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    public partial class TimelineRequestSystem : SystemBase
    {
        EntityQuery _requestQuery;
        EntityQuery _closedQuery;
        int _sequence;

        protected override void OnCreate()
        {
            base.OnCreate();
            _requestQuery = GetEntityQuery(ComponentType.ReadOnly<TimelineRequest>(), ComponentType.ReadWrite<Playback>());
            _closedQuery = GetEntityQuery(ComponentType.ReadOnly<TimelineLibrary>());
            RequireForUpdate<TimelineLibrary>();
        }

        protected override void OnUpdate()
        {
            var view = SystemAPI.GetSingleton<TimelineLibrary>().View;
            if (!view.Kinds.IsEmpty)
                return;
            using (var entities = _requestQuery.ToEntityArray(Allocator.Temp))
            {
                if (entities.Length == 0)
                    return;
                var gathered = new NativeList<RequestEntry>(entities.Length * 2, Allocator.Temp);
                for (var e = 0; e < entities.Length; e++)
                {
                    var requests = EntityManager.GetBuffer<TimelineRequest>(entities[e]);
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
                var sorted = new NativeArray<RequestEntry>(gathered.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                sorted.CopyFrom(gathered.AsArray());
                unsafe
                {
                    TimelineKernel.SortRequests((RequestEntry*)sorted.GetUnsafePtr(), sorted.Length);
                }
                using (var closed = new NativeList<ClosedPlayback>(gathered.Length, Allocator.Temp))
                {
                    var sink = closed;
                    for (var i = 0; i < sorted.Length; i++)
                    {
                        var entry = sorted[i];
                        var target = FindEntity(entities, entry.Entity);
                        if (!target.HasValue)
                            continue;
                        var playbacks = EntityManager.GetBuffer<Playback>(target.Value);
                        ApplyEntry(ref playbacks, entry, view, ref sink);
                        EntityManager.GetBuffer<TimelineRequest>(target.Value).Clear();
                    }
                    PublishClosed(closed);
                }
            }
        }

        static Entity? FindEntity(NativeArray<Entity> entities, int index)
        {
            for (var i = 0; i < entities.Length; i++)
                if (entities[i].Index == index)
                    return entities[i];
            return null;
        }

        static unsafe void ApplyEntry(ref DynamicBuffer<Playback> playbacks, RequestEntry entry, global::Tl.LibraryView view, ref NativeList<ClosedPlayback> closed)
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
                default:
                    TimelineKernel.ApplyStopAll(ref playbacks, entity, ref closed);
                    break;
            }
        }

        void PublishClosed(NativeList<ClosedPlayback> closed)
        {
            using (var holders = _closedQuery.ToEntityArray(Allocator.Temp))
            {
                if (holders.Length == 0 || closed.Length == 0)
                    return;
                var sink = EntityManager.GetBuffer<ClosedPlayback>(holders[0]);
                for (var i = 0; i < closed.Length; i++)
                    sink.Add(closed[i]);
            }
        }
    }

    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineRequestSystem))]
    public partial class TimelineAdvanceSystem : SystemBase
    {
        EntityQuery _playbackQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _playbackQuery = GetEntityQuery(ComponentType.ReadWrite<Playback>());
            RequireForUpdate<TimelineLibrary>();
        }

        protected override void OnUpdate()
        {
            var view = SystemAPI.GetSingleton<TimelineLibrary>().View;
            if (!view.Kinds.IsEmpty)
                return;
            Dependency = TimelineJobs.ScheduleAdvance(this, _playbackQuery, view, Dependency);
        }
    }

    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineAdvanceSystem))]
    public partial class TimelineEventSystem : SystemBase
    {
        EntityQuery _closedQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _closedQuery = GetEntityQuery(ComponentType.ReadWrite<ClosedPlayback>());
        }

        protected override void OnUpdate()
        {
            using (var holders = _closedQuery.ToEntityArray(Allocator.Temp))
            {
                for (var i = 0; i < holders.Length; i++)
                    EntityManager.GetBuffer<ClosedPlayback>(holders[i]).Clear();
            }
        }
    }
}
