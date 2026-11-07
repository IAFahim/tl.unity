using SpikeA;
using Unity.Burst;
using Unity.Entities;

namespace SpikeAGame
{
    [BurstCompile(CompileSynchronously = true)]
    public partial struct DoubleJob : IJobEntity
    {
        public void Execute(ref SpikeHealth health)
        {
            health.Value *= 2f;
            Mark();
        }

        [BurstDiscard]
        static void Mark() => SpikeProbe.Managed++;
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct SpikeEntitySystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            new DoubleJob().ScheduleParallel();
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct SpikeQuerySystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var health in SystemAPI.Query<RefRW<SpikeHealth>>())
                health.ValueRW.Value += 100f;
        }
    }
}
