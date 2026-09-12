using Unity.Burst;
using Unity.Entities;

namespace Tl.Samples.GeneratedJobs
{
    public struct Clock : IComponentData
    {
        public uint GameTick;
        public int Delta;
    }

    [BurstCompile]
    public partial struct GeneratedTimelineSystem : ISystem
    {
        private Combat.Scheduler _scheduler;

        public void OnCreate(ref SystemState state)
        {
            _scheduler.OnCreate(ref state);
            state.RequireForUpdate<Clock>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<Clock>();
            _scheduler.Tick(ref state, clock.GameTick, clock.Delta);
        }
    }
}
