using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;
using Playback = Tl.Unity.Playback;

public struct HasteSchedule : IComponentData
{
    public TlTimeline Haste;
    public ushort First;
    public ushort Second;
}

public sealed class HasteBaker : Baker<HasteAuthoring>
{
    public override void Bake(HasteAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.None);
        AddComponent(entity, new HasteSchedule { Haste = TlBaking.GetTimeline(this, authoring.Haste), First = (ushort)authoring.First, Second = (ushort)authoring.Second });
    }
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct HasteSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new StartHaste().Schedule();
    }
}

[BurstCompile]
public partial struct StartHaste : IJobEntity
{
    void Execute(in HasteSchedule schedule, in DynamicBuffer<Playback> playbacks, ref DynamicBuffer<TimelineRequest> requests)
    {
        if (playbacks.Length == 0 || playbacks[0].Timeline == 0)
            return;
        var next = playbacks[0].Tick + 1;
        if (next == schedule.First || next == schedule.Second)
            requests.Start(schedule.Haste);
    }
}
