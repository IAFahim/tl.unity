using Tl;
using Unity.Burst;
using Unity.Entities;

[TlClip("Then Play")]
public struct ThenPlayClip
{
    public TlTimeline Timeline;
    public TimelineId Fallback;
    public float Gain;
}

[TlKind]
public struct ControlKind
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref ControlValue value) => value.Value += total;
}

public struct ControlValue : IComponentData
{
    public float Value;
}

public struct ThenPlayTrack : ITlTrack<ThenPlayClip>
{
    [Feeds(typeof(ControlKind))]
    public static float Evaluate(in TlFrame<ThenPlayClip> frame) => frame.Clip.Gain;
}

public struct ThenPlay : IComponentData
{
    public TlTimeline Timeline;
    public byte Done;
}

public struct Trigger : IComponentData
{
    public byte Fire;
    public byte Hold;
    public byte Release;
    public byte Speed;
}

public struct ThenPlayBaking : ITlBaker<ThenPlayClip>
{
    public void Bake(ref TlBaker baker, in ThenPlayClip clip)
    {
        baker.AddComponent(new ThenPlay { Timeline = clip.Timeline });
        baker.AddComponent<Trigger>();
    }
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderFirst = true)]
public partial struct ThenPlaySystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new StartWhenTriggered().ScheduleParallel();
    }
}

[BurstCompile(CompileSynchronously = true)]
public partial struct StartWhenTriggered : IJobEntity
{
    void Execute(ref ThenPlay then, ref Trigger trigger, DynamicBuffer<Tl.Unity.TimelineRequest> requests)
    {
        ControlProbe.MarkManaged();
        if (trigger.Fire != 0 && then.Done == 0)
        {
            requests.Start(then.Timeline);
            then.Done = 1;
        }
        if (trigger.Hold != 0)
        {
            requests.Pause(then.Timeline);
            trigger.Hold = 0;
        }
        if (trigger.Release != 0)
        {
            requests.Resume(then.Timeline);
            trigger.Release = 0;
        }
        if (trigger.Speed != 0)
        {
            requests.SetSpeed(then.Timeline, trigger.Speed);
            trigger.Speed = 0;
        }
    }
}

#if UNITY_7000_0_OR_NEWER
[global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
public static class ControlProbe
{
    public static bool RanManaged;

    [BurstDiscard]
    public static void MarkManaged() => RanManaged = true;
}
