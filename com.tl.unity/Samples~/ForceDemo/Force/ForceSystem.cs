using Tl;
using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct ForceSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new ApplyForce { DeltaTime = SystemAPI.Time.DeltaTime }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct ApplyForce : IJobEntity
{
    public float DeltaTime;

    void Execute(ref LocalTransform transform, ref Force force)
    {
        transform.Position += force.Value * DeltaTime;
        force.Value = default;
    }
}
