using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    public interface ITlKindJobs
    {
        void Create(ref SystemState state);
        JobHandle Schedule(ref SystemState state, LibraryView view, KindIndex index, bool forward, JobHandle dependsOn);
    }

    [UpdateInGroup(typeof(TlSimulationGroup))]
    [UpdateAfter(typeof(TimelineSimulationGroup))]
    public partial class TlFeedGroup : ComponentSystemGroup
    {
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TlFeedGroup))]
    public partial struct TlFeedSystem<TJobs> : ISystem
        where TJobs : unmanaged, ITlKindJobs
    {
        EntityQuery _library;
        TJobs _jobs;

        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineLibrary, TimelineStep>();
            _library = builder.Build(ref state);
            builder.Dispose();
            state.RequireForUpdate(_library);
            _jobs.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = _library.GetSingleton<TimelineLibrary>();
            var step = _library.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TlSimulationGroup))]
    [UpdateAfter(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TlFeedGroup))]
    public partial struct TlKindSystem<TJobs> : ISystem
        where TJobs : unmanaged, ITlKindJobs
    {
        EntityQuery _library;
        TJobs _jobs;

        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineLibrary, TimelineStep>();
            _library = builder.Build(ref state);
            builder.Dispose();
            state.RequireForUpdate(_library);
            _jobs.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = _library.GetSingleton<TimelineLibrary>();
            var step = _library.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }
}
