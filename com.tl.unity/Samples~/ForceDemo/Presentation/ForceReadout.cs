using Tl;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

public struct ForceShown : IComponentData
{
    public float3 Value;
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
[UpdateBefore(typeof(ForceSystem))]
public partial struct ForceShownSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new ShowForce().ScheduleParallel();
    }
}

[BurstCompile]
public partial struct ShowForce : IJobEntity
{
    void Execute(ref ForceShown shown, in Force force)
    {
        shown.Value = force.Value;
    }
}
