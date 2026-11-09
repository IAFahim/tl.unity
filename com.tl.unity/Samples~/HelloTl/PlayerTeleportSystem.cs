using HelloTl;
using Tl;
using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct PlayerTeleportSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (playerTransform, teleported) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<Teleported>>())
            playerTransform.ValueRW.Position.x = teleported.ValueRO.Offset;
    }
}
