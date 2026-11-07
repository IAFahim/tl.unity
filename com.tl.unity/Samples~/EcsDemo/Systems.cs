using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

namespace Demo
{
    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct MovementSystem : ISystem
    {
        TimelineJobs.Movement _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Movement.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct VitalsSystem : ISystem
    {
        TimelineJobs.Vitals _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Vitals.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }
}
