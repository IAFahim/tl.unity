# Using Tl in Unity ECS

Tl plays timelines on entities with Burst `ISystem`s over unmanaged data. A designer builds the timelines in Unity's own Timeline window;
a coder writes a clip struct, a kind and a track. There is no extra file, asset, menu step or generated file to keep.

Tested on the newest Unity 7 editor (7000.0.0a16) with Entities 7.0.0, Entities Graphics 7.0.0, Timeline and URP.

Other guides:

- How Timeline assets become entity data, preview and live edit: [UNITY-TIMELINE.md](UNITY-TIMELINE.md).
- Plain .NET host without ECS: [USING-PLAIN-CSHARP.md](USING-PLAIN-CSHARP.md).
- Kinds, capacities and the Kind Report: [KINDS.md](KINDS.md).
- The 3-minute check in a real Editor: [FORCE-DEMO-CHECK.md](FORCE-DEMO-CHECK.md).

## 1. Install

1. Unity Hub > New project > **Universal 3D** (URP). Entities Graphics draws SubScene objects only under URP or HDRP; a Built-in project
   runs Tl but shows nothing.
2. `Window > Package Management > Package Manager` > **+** > **Install package from git URL** >
   `https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main`.
3. Optional: Package Manager > **Tl Unity Playback** > **Samples** > **Force demo** > **Import**. The scene opens by itself; press Play.

Or from a terminal: the installers in IAFahim/tl (`install/unity.sh`, `install/unity.ps1`) create a URP project with the package and the
Force demo ready (section 8).

## 2. The coder's part: five small files

This is the whole Force track. No asmdef is needed (the files can live in `Assets/`); with your own asmdef, reference `Tl.Unity` and
`Unity.Timeline`.

```csharp
using Tl;
using Unity.Mathematics;

[TlClip("Force")]
public struct ForceClip
{
    public float3 Direction;
    public float Strength;
    public TlCurve Falloff;
}
```

```csharp
using Unity.Entities;
using Unity.Mathematics;

public struct Force : IComponentData { public float3 Value; }
public struct ForceLog : IBufferElementData { public float3 Value; public int Tick; }
public struct ForceFalloff : IComponentData { public BlobAssetReference<TlCurveBlob> Curve; }
```

```csharp
using Tl;
using Unity.Mathematics;

[TlKind]
public struct ForceKind
{
    public static void Combine(ref float3 total, in float3 next) => total += next;
    public static void Apply(in float3 total, ref Force force) => force.Value += total;
}

public struct ForceTrack : ITlTrack<ForceClip>
{
    [Feeds(typeof(ForceKind))]
    public static float3 Evaluate(in TlFrame<ForceClip> frame)
        => frame.Clip.Direction * frame.Clip.Strength * frame.Clip.Falloff.Evaluate(frame.Progress);
}
```

```csharp
using Tl;

public struct ForceBaking : ITlBaker<ForceClip>
{
    public void Bake(ref TlBaker baker, in ForceClip clip)
    {
        baker.AddComponent<Force>();
        baker.AddBuffer<ForceLog>();
        baker.AddBlob(new ForceFalloff { Curve = baker.CreateCurve(clip.Falloff) });
    }
}
```

```csharp
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
```

What each piece is:

| Name | Meaning |
|---|---|
| `[TlClip("Force")]` | the clip's data. It shows in the Timeline window's **+** menu as **Force**, and its public fields are the clip Inspector. `TlCurve` is an `AnimationCurve` there; `TlTimeline` and `TimelineId` are Timeline object fields. |
| `[TlKind]` | how values from overlapping clips combine (`Combine`) and where the result goes (`Apply`, into your own `IComponentData`). |
| `ITlTrack<TClip>` + `[Feeds]` | evaluates one clip at the current tick. `TlFrame` gives `Clip`, `Tick`, `ClipLength`, `WithinClip`, `Progress` (0 to 1). |
| `ITlBaker<TClip>` | optional: components the object needs because it plays this clip. Runs once per clip. `AddComponent`/`AddBuffer` add once; `AddBlob` and `AddComponent(value)` keep the first value and warn only when a later clip sets a different one. Without it, the components written by `Apply` are added anyway. |
| `TlSimulationGroup` | the fixed-step group Tl ticks in (60 ticks per second by default, `Project Settings > Tl`). `OrderLast` runs after the kinds were applied. |

A kind's entry buffer gets an inline capacity of 128 bytes / entry size; `[TlKind(Inline = n)]` sets it. Editing a timeline never compiles
code.

## 3. The designer's part

1. Project window > `Create > Tl > Timeline` (or Unity's own Timeline asset; set its frame rate to the tick rate).
2. Open it in `Window > Sequencing > Timeline`, click **+** > **Force**. Right-click the track > **Add Tl Clip**. Set Direction, Strength
   and the Falloff curve in the Inspector.
3. In a SubScene, add a **Playable Director** to an object, drag the timeline into **Playable**, tick **Play On Awake**.
4. Press Play. The object is pushed.

Optional, all in the Inspector:

- Timeline asset > **Tl** section: Channel (only one timeline plays per channel), When it ends (Stop, Loop, Hold, Play next).
- Playable Director > **Tl** foldout: **Also Plays** (more timelines this object can play), what it plays, and plain messages when
  something is missing.
- While Play runs, change a clip: the object follows within a second and keeps its time. Adding or removing a type of track needs a
  restart of Play (one console message says so).
- Without Play, drag the Timeline window playhead: the object moves in the Scene view exactly as Play would move it at that time.

## 4. Starting, stopping and pausing from code

Give the clip a `TlTimeline` field; the designer drags a timeline into it. Bake it into your own component, then use the `TlRequests`
methods on the object's `DynamicBuffer<TimelineRequest>`. They only append to the buffer, so they work inside Burst jobs.

```csharp
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
```

- `requests.Start(timeline, atTick)`, `Stop(timeline)`, `Pause(timeline)`, `Resume(timeline)`. A paused timeline holds its tick; its clips
  stay active.
- A timeline named in a clip field is added to the object automatically. A timeline the object cannot play is dropped with one warning:
  drag it into **Also Plays**.
- `TimelineId` fields in clips (for Tl's own clip logic) are written as library ids when the library is assembled.
- The Control demo in the Force demo sample (`Tl > Samples > Build Control Demo`) runs exactly this code.

## 5. What runs where

| Stage | System | Notes |
|---|---|---|
| bake | `TlDirectorBaker` (Editor) | one blob per timeline on the director's entity, kind buffers and your components; incremental with `DependsOn` |
| load | `TlLibrarySystem`, `TlTimelineRegisterSystem` (Initialization) | the library is assembled from the loaded blobs; nothing per frame once registered |
| start | `TlPlayOnAwakeSystem` | Play On Awake and Initial Time |
| tick | `TlRequestResolveSystem`, `TimelineKindTickSystem` (Burst) | requests, one tick per fixed step, one kind entry per active clip |
| apply | generated `TlKindSystem<...>` (Burst jobs) | `Combine` then `Apply` into your components |
| yours | anything in `TlSimulationGroup` | here `ForceSystem` |

The tick path allocates nothing (0 B per warm frame) and holds no managed references.

## 6. Messages you can see

| Where | Message | What to do |
|---|---|---|
| Director foldout | This object is not in a SubScene, so Tl will not run it. | move it into a SubScene |
| Director foldout | This object has no timeline. Drag one into Playable. | |
| Timeline Tl section | No Tl tracks yet. In the Timeline window click + and pick a Tl track. | |
| Console, baking | Another clip on Cube already sets ForceFalloff with a different value. Only the first is kept on the object. | make the values equal or split the objects |
| Console, Play | the change to timeline 'Push' adds or removes a type of track ... the earlier version keeps playing. | stop and press Play again |
| Console, Play | code asked an object to play a timeline it cannot play. | add it to Also Plays |
| Console, on import | The Force demo cube lives in a SubScene ... Built-in Render Pipeline ... | use a URP project |

## 7. Tests and players

- `eng/test-com-tl-unity.sh` runs the package's EditMode and PlayMode tests (both pinned hashes, Burst, live edit, preview, control).
- The Force demo sample carries its own PlayMode and Edit mode tests (push 4.0333 m, live edit, scrub preview, Control demo).
- IL2CPP and CoreCLR players: the **Kinds IL2CPP player** sample prints both pinned hashes; the Force demo runs in a player as is.

## 8. Installers

`install/unity.ps1` (Windows) and `install/unity.sh` (Linux, macOS, Git Bash) in IAFahim/tl create a URP project with the newest Unity 7
editor, add the package from its git URL, import the Force demo, build its scene with a camera and run its PlayMode test. Nothing else is
installed.
