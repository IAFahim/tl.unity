using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public struct FollowCamera : IComponentData
{
    public float3 Offset;
    public float Sharpness;
}

public struct FollowTarget : IComponentData
{
}

[BurstCompile]
[UpdateInGroup(typeof(TransformSystemGroup), OrderFirst = true)]
public partial struct FollowCameraSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<FollowTarget>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var target = SystemAPI.GetSingletonEntity<FollowTarget>();
        new FollowJob
        {
            Target = SystemAPI.GetComponent<LocalTransform>(target).Position,
            DeltaTime = SystemAPI.Time.DeltaTime
        }.Schedule();
    }
}

[BurstCompile]
public partial struct FollowJob : IJobEntity
{
    public float3 Target;
    public float DeltaTime;

    void Execute(ref LocalTransform transform, in FollowCamera follow)
    {
        var goal = Target + follow.Offset;
        transform.Position = math.lerp(transform.Position, goal, 1f - math.exp(-follow.Sharpness * DeltaTime));
    }
}
