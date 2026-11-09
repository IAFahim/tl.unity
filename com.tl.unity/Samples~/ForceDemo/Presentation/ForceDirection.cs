using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
[UpdateBefore(typeof(ForceSystem))]
public partial struct ForceDirectionSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var forward = true;
        foreach (var step in SystemAPI.Query<RefRO<TimelineStep>>())
            forward = step.ValueRO.Forward;
        new TurnForceAround { WorldForward = forward }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct TurnForceAround : IJobEntity
{
    public bool WorldForward;

    void Execute(ref Force force, in DynamicBuffer<Tl.Unity.Playback> playbacks)
    {
        for (var i = 0; i < playbacks.Length; i++)
        {
            var playback = playbacks[i];
            if (playback.Timeline == 0 || (playback.Flags & (Tl.PlaybackFlags.Paused | Tl.PlaybackFlags.Ended)) != 0)
                continue;
            if (WorldForward == ((playback.Flags & Tl.PlaybackFlags.Reverse) != 0))
                force.Value = -force.Value;
            return;
        }
    }
}
