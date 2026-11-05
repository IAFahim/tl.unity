using System;
using Tl;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{

public struct Motion { public float X, Y; }
public struct Health { public int Value; }
public struct Stamina { public int Value; }
public struct WalkStat { public float Value; }
public struct JumpPower { public float Value; }
public struct Speed { public float Value; public byte Priority; }
public struct Target { public Entity Value; }
public struct Anchor : IComponentData { public float Boost; }

public struct AnchorLookup
{
    [ReadOnly] public ComponentLookup<Anchor> Anchors;

    public float Boost(in Target target) => Anchors.TryGetComponent(target.Value, out var anchor) ? anchor.Boost : 1f;
}

public struct WalkClip { public float Speed; public byte Priority; }
public struct WalkTrack : IBlend<WalkClip>
{
    public float Scale;
    public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result) => result = first;
}

public struct ArcClip { public float Velocity; }
public struct ArcTrack : IBlend<ArcClip>
{
    public float Scale;
    public void Blend(in ArcClip first, in ArcClip second, float factor, out ArcClip result) => result = first;
}

public struct HitClip { public int Amount; }
public struct HitTrack : IBlend<HitClip>
{
    public float Scale;
    public void Blend(in HitClip first, in HitClip second, float factor, out HitClip result) => result = first;
}

public struct StatClip { public int Delta; }
public struct StatTrack : IBlend<StatClip>
{
    public float Scale;
    public void Blend(in StatClip first, in StatClip second, float factor, out StatClip result) => result = first;
}

public struct StepMarker { public int Delta; }

[TimelineKind]
public struct Locomotion
{
    public static void Combine(ref Speed total, in Speed next) { if (next.Priority >= total.Priority) total = next; }
    public static void Apply(in Speed total, ref Motion motion) => motion.X += total.Value;
}

[TimelineKind]
public struct Lift
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref Motion motion) => motion.Y += total;
}

[TimelineKind(Accepts = KindEntries.Pulses)]
public struct Damage
{
    public static void Combine(ref int total, in int next) => total += next;
    public static void Apply(in int total, ref Health health) => health.Value -= total;
}

[TimelineKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
public struct StatChange
{
    public static void Apply(in int delta, ref Stamina stamina) => stamina.Value += delta;
}

public struct Walk : ITrack<WalkTrack, WalkClip>
{
    [Feeds(typeof(Locomotion))]
    public static Speed Evaluate(in Frame<WalkTrack, WalkClip> f, in WalkStat stat, in Target target, in AnchorLookup anchors)
        => new Speed { Value = f.Direction * f.Clip.Speed * f.Track.Scale * stat.Value * anchors.Boost(in target), Priority = f.Clip.Priority };
}

public struct Jump : ITrack<ArcTrack, ArcClip>
{
    public static void Fold(in Frame<ArcTrack, ArcClip> f, out float lift) => lift = f.Direction * f.Clip.Velocity * f.Track.Scale;

    [Feeds(typeof(Lift))]
    public static float Evaluate(in float lift, in JumpPower power) => lift * power.Value;
}

[TimelineSystemMember(typeof(Vitals))]
public struct Hit : ITrack<HitTrack, HitClip>
{
    [Pulse(typeof(Damage), On = ClipEvent.Start)]
    public static int Start(in Frame<HitTrack, HitClip> f) => f.Direction > 0 ? f.Clip.Amount : 0;
}

[TimelineSystemMember(typeof(Movement))]
public struct ChangeStat : ITrack<StatTrack, StatClip>
{
    [Pulse(typeof(StatChange), On = ClipEvent.Start)]
    public static int Start(in Frame<StatTrack, StatClip> f) => f.Direction * f.Clip.Delta;
}

[TimelineSystemMember(typeof(Movement))]
public struct Steps : IMarker<StepMarker>
{
    [Pulse(typeof(StatChange), On = ClipEvent.Marker)]
    public static int OnStep(in MarkerFrame<StepMarker> m) => m.Marker.Delta;
}

[TimelineSystem]
public ref partial struct Movement
{
    public Span<Motion> Motion;
    public ReadOnlySpan<WalkStat> WalkStat;
    public ReadOnlySpan<JumpPower> JumpPower;
    public ReadOnlySpan<Target> Target;
    public AnchorLookup Anchors;
    public Span<Stamina> Stamina;
}

[TimelineSystem]
public ref partial struct Vitals
{
    public Span<Health> Health;
}

}
