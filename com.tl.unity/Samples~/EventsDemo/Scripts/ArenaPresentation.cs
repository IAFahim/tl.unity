using Tl;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public struct Home : IComponentData
{
    public float3 Value;
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
[UpdateBefore(typeof(StrikeSystem))]
public partial struct ArenaPoseSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new LungeToTransform().ScheduleParallel();
        new SquashToScale().ScheduleParallel();
    }
}

[BurstCompile]
public partial struct LungeToTransform : IJobEntity
{
    void Execute(ref LocalTransform transform, ref Lunge lunge, in Home home)
    {
        transform.Position = home.Value + new float3(lunge.Value, 0f, 0f);
        lunge.Value = 0f;
    }
}

[BurstCompile]
public partial struct SquashToScale : IJobEntity
{
    void Execute(ref PostTransformMatrix scale, ref Squash squash)
    {
        scale.Value = float4x4.Scale(1f + 0.5f * squash.Value, 1f - squash.Value, 1f + 0.5f * squash.Value);
        squash.Value = 0f;
    }
}

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct SparkSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<Spark>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new FadeSparks
        {
            DeltaTime = SystemAPI.Time.DeltaTime,
            Commands = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter()
        }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct FadeSparks : IJobEntity
{
    public float DeltaTime;
    public EntityCommandBuffer.ParallelWriter Commands;

    void Execute(Entity entity, [ChunkIndexInQuery] int chunk, ref Spark spark, ref LocalTransform transform)
    {
        spark.Life -= DeltaTime;
        transform.Scale = math.max(0.05f, transform.Scale + DeltaTime * 1.5f);
        if (spark.Life <= 0f)
            Commands.DestroyEntity(chunk, entity);
    }
}
