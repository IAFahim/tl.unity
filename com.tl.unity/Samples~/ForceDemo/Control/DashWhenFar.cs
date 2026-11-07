using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[TlClip("Dash When Far")]
public struct DashWhenFarClip
{
    public TlTimeline Dash;
    public float AtX;
}

public struct DashWhenFarTrack : ITlTrack<DashWhenFarClip>
{
    [Feeds(typeof(ForceKind))]
    public static float3 Evaluate(in TlFrame<DashWhenFarClip> frame) => float3.zero;
}

public struct DashWhenFar : IComponentData
{
    public TlTimeline Dash;
    public float AtX;
    public bool Done;
}

public struct DashWhenFarBaking : ITlBaker<DashWhenFarClip>
{
    public void Bake(ref TlBaker baker, in DashWhenFarClip clip)
    {
        baker.AddComponent(new DashWhenFar { Dash = clip.Dash, AtX = clip.AtX });
    }
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderFirst = true)]
public partial struct DashWhenFarSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new StartDashWhenFar().ScheduleParallel();
    }
}

[BurstCompile]
public partial struct StartDashWhenFar : IJobEntity
{
    void Execute(ref DashWhenFar dash, in LocalTransform transform, DynamicBuffer<TimelineRequest> requests)
    {
        if (dash.Done || transform.Position.x < dash.AtX)
            return;
        requests.Start(dash.Dash);
        dash.Done = true;
    }
}
