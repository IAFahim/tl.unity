using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

namespace ScaleGame.Benchmark
{
    [BurstCompile]
    public partial struct ScaleSystem : ISystem
    {
        ScaleGame.TimelineJobs.Scale _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = ScaleGame.TimelineJobs.Scale.Create(ref state);
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
