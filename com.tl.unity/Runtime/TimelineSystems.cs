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
            var gathered = new NativeList<global::Tl.RequestRecord>(entities.Length * 2, Allocator.Temp);
            for (var e = 0; e < entities.Length; e++)
            {
                var requests = manager.GetBuffer<TimelineRequest>(entities[e]);
                for (var r = 0; r < requests.Length; r++)
                    gathered.Add(new global::Tl.RequestRecord(entities[e].Index, requests[r].Slot, requests[r].Timeline, requests[r].At, requests[r].Op, _sequence++));
            }
            var scratch = new NativeArray<global::Tl.RequestRecord>(gathered.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            global::Tl.RequestQueue.Sort((global::Tl.RequestRecord*)gathered.GetUnsafePtr(), gathered.Length, (global::Tl.RequestRecord*)scratch.GetUnsafePtr());
            var closed = new NativeList<ClosedPlayback>(gathered.Length, Allocator.Temp);
            var byIndex = new NativeHashMap<int, Entity>(entities.Length, Allocator.Temp);
            for (var e = 0; e < entities.Length; e++)
                byIndex[entities[e].Index] = entities[e];
            for (var i = 0; i < gathered.Length; i++)
            {
                var entry = gathered[i];
                if (!byIndex.TryGetValue(entry.Entity, out var target))
                    continue;
                var slots = new PlaybackSlots(manager.GetBuffer<Playback>(target), entry.Entity, closed);
                global::Tl.PlaybackRules.Apply(ref slots, view.Timelines, view.TimelineCount, entry.Op, entry.Timeline, entry.At);
                var drained = manager.GetBuffer<TimelineRequest>(target);
                drained.Clear();
                drained.TrimExcess();
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
            var forward = !SystemAPI.TryGetSingleton<TimelineStep>(out var step) || step.Forward;
            state.Dependency = TimelineJobs.ScheduleAdvance(ref state, _playbackQuery, view, forward, state.Dependency);
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
