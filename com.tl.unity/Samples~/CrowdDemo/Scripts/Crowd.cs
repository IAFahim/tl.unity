using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

public struct Crowd : IComponentData
{
    public Entity Prefab;
    public int Count;
    public int Spawned;
    public float Spacing;
    public TlTimeline Dance;
    public ushort Ticks;
}

public struct Dancer : IComponentData
{
    public float3 Home;
}

[BurstCompile]
public partial struct CrowdSpawnSystem : ISystem
{
    EntityQuery _dancers;

    public void OnCreate(ref SystemState state)
    {
        _dancers = SystemAPI.QueryBuilder().WithAllRW<Dancer, LocalTransform>().WithAllRW<TimelineRequest>().Build();
        state.RequireForUpdate<Crowd>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var crowd = SystemAPI.GetSingleton<Crowd>();
        if (crowd.Spawned == crowd.Count)
            return;
        state.EntityManager.DestroyEntity(_dancers);
        var count = math.max(0, crowd.Count);
        var dancers = state.EntityManager.Instantiate(crowd.Prefab, count, Allocator.Temp);
        dancers.Dispose();
        crowd.Spawned = count;
        SystemAPI.SetSingleton(crowd);
        state.Dependency = new PlaceDancers
        {
            Crowd = crowd,
            Side = math.max(1, (int)math.ceil(math.sqrt(count)))
        }.ScheduleParallel(_dancers, state.Dependency);
    }
}

[BurstCompile]
public partial struct PlaceDancers : IJobEntity
{
    public Crowd Crowd;
    public int Side;

    void Execute([EntityIndexInQuery] int i, ref LocalTransform transform, ref Dancer dancer, DynamicBuffer<TimelineRequest> requests)
    {
        var x = i % Side;
        var z = i / Side;
        dancer.Home = new float3(x - Side * 0.5f, 0f, z) * Crowd.Spacing;
        transform.Position = dancer.Home;
        requests.Start(Crowd.Dance, (ushort)((x + z) * 3 % math.max(1, (int)Crowd.Ticks)));
    }
}

[BurstCompile]
[UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
[UpdateAfter(typeof(TlSimulationGroup))]
public partial struct DancePoseSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new PoseDancers().ScheduleParallel();
    }
}

[BurstCompile]
public partial struct PoseDancers : IJobEntity
{
    void Execute(ref LocalTransform transform, ref DancePose pose, ref URPMaterialPropertyBaseColor color, in Dancer dancer)
    {
        transform.Position = dancer.Home + new float3(0f, pose.Lift, 0f);
        transform.Rotation = quaternion.RotateY(math.radians(pose.Yaw));
        var glow = math.saturate(pose.Glow);
        color.Value = new float4(math.lerp(new float3(0.25f, 0.45f, 0.95f), new float3(1f, 0.8f, 0.3f), glow), 1f);
        pose.Lift = 0f;
        pose.Yaw = 0f;
        pose.Glow = 0f;
    }
}
