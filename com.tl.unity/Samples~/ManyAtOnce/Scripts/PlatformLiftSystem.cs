using Tl;
using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

namespace ManyAtOnce
{
    [BurstCompile]
    [UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
    public partial struct PlatformLiftSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (platformTransform, platform) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<Platform>>())
                platformTransform.ValueRW.Position.y = platform.ValueRO.Height;
        }
    }
}
