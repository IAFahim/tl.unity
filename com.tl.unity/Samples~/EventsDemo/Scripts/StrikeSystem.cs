using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct StrikeSystem : ISystem
{
    EntityQuery _attackers;
    EntityQuery _tick;

    public void OnCreate(ref SystemState state)
    {
        _attackers = SystemAPI.QueryBuilder().WithAll<Attacker>().WithAllRW<Score, Strikes>().Build();
        _tick = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineKindTick>().WithOptions(EntityQueryOptions.IncludeSystems).Build(ref state);
        state.RequireForUpdate(_attackers);
        state.RequireForUpdate(_tick);
        state.RequireForUpdate<EndFixedStepSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var tick = _tick.GetSingletonRW<TimelineKindTick>();
        var sender = tick.ValueRW.BeginPulses(_attackers.CalculateChunkCountWithoutFiltering());
        state.Dependency = new ReactToStrikes
        {
            Writer = sender.Writer,
            Damage = TlUnityGenerated.ArenaDamageKind.Pulses(sender.Writer),
            Requests = SystemAPI.GetBufferLookup<TimelineRequest>(),
            Positions = SystemAPI.GetComponentLookup<LocalTransform>(true),
            Commands = SystemAPI.GetSingleton<EndFixedStepSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged)
        }.Schedule(_attackers, state.Dependency);
        tick.ValueRW.AddPulseWriter(state.Dependency);
    }
}

[BurstCompile]
public partial struct ReactToStrikes : IJobEntity, IJobEntityChunkBeginEnd
{
    public KindPulseWriter Writer;
    public Tl.Unity.KindPulses<int> Damage;
    public BufferLookup<TimelineRequest> Requests;
    [ReadOnly] public ComponentLookup<LocalTransform> Positions;
    public EntityCommandBuffer Commands;

    public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        Writer.Begin(unfilteredChunkIndex);
        return true;
    }

    public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask, bool chunkWasExecuted)
    {
        Writer.End();
    }

    void Execute(in Attacker attacker, ref Score score, ref Strikes strikes)
    {
        score.Ticks++;
        if (strikes.Count == 0)
            return;
        Damage.To(attacker.Target, strikes.Amount);
        for (var i = 0; i < strikes.Count; i++)
        {
            score.Strikes++;
            if (Requests.HasBuffer(attacker.Target) && attacker.Hurt.IsValid)
            {
                Requests[attacker.Target].Start(attacker.Hurt);
                score.Hurts++;
            }
            if (attacker.Spark != Entity.Null && Positions.HasComponent(attacker.Target))
            {
                var spark = Commands.Instantiate(attacker.Spark);
                Commands.SetComponent(spark, LocalTransform.FromPosition(Positions[attacker.Target].Position + new float3(-0.45f, 1.1f, 0f)));
                score.Sparks++;
            }
        }
        strikes = default;
    }
}