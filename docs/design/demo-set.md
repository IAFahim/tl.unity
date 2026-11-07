# Demo set plan (issue #22)

Status: wave 1 built for A (Force polish), G (Events) and H (Crowd, Unity half), plus a Demo hub sample and the installers in tl
(all demos in one project, one result line per demo). B to F are not built. Differences from this plan, found while building:
- G sends the strike with `[Pulse]` into a `Mode = Each` kind (`ArenaStrike`) instead of `Events<Strike>`: the zero-config generator
  does not dispatch `OnClipStart` + `Events<T>` on `ITlTrack` (IAFahim/tl.unity#25). The dummy's health is `DummyHealth.Max` minus a
  `Wounds` kind column, because Tl adds every auto-system column to every entity a Tl timeline reaches (IAFahim/tl.unity#26), so the
  game baker keeps its starting value in a component no kind writes.
- `TlBaking.GetTimeline(baker, timeline)` and `TlBaking.Ticks(seconds)` exist (package addition); the target object must have the
  timeline on its Playable Director or Also Plays.
- H's bench steps `TlSimulationGroup` in the running default world (200 steps, jobs completed after each, second run kept).

Based on tl.unity main `9328184` and tl main `f07c61c5`.

Why: the owner scored the demo 3/10. Today there is one Force demo and a small Control demo. They do not show most of Tl, there is no
way to control a timeline from a UI, and everything is Unity-only. This plan replaces that with eight demos. Each one shows one thing a
real developer does. Five of them also run in a .NET console with no engine, using the same data and giving the same hash.

Fits the engine-agnostic plan being written in parallel: timelines are portable JSON (baked to TLB), `tl run` is a .NET CLI, kind packs
are plain libraries, Unity is a thin adapter. Where this plan needs something from that work it says **EA** and names the piece (section 4).

Rules kept: newest Unity 7 only; hot path is Burst `ISystem` + `IJobEntity`/`IJob`, never `SystemBase` or main-thread entity loops;
designers drag and drop, never type names; no `AssetDatabase.Load*`/`Resources.Load` outside tests; scenes and assets are made by
builder scripts through Unity, never hand-written YAML.

Short names used below:
- **A** Force (exists, polished)
- **B** Playback panel
- **C** Animation
- **D** Inventory (CLI only)
- **E** Stats and buffs
- **F** Same data, two engines
- **G** Events to gameplay
- **H** Crowd (many entities, bench)

---

## 1. Coverage: every Tl feature against the demo that shows it

"Unity" means the feature is shown in a Unity scene, "CLI" means in the .NET console.

| Feature (what a dev uses) | Shown by | Notes |
|---|---|---|
| Clip type `[TlClip]`, typed Inspector fields (numbers, bool, enum, vectors) | A, C, E (Unity); D, E (CLI JSON) | |
| `TlCurve` (designer curve, sampled to 16 points) | A, C | |
| `TlTimeline` field in a clip (data starts another timeline) | A (Control part), G | |
| `TimelineId` field in a clip (Tl's own logic starts a timeline) | none | tested only (`ClipTimelineFieldsJoinTheSet...`). Could be added to G later |
| Kind, `Combine` by sum | A, E | |
| Kind, `Combine` by priority (override) | C | |
| Kind `Mode = Each` (every pulse applied once) | D, E | |
| `Accepts` Spans / Pulses / Both | C (spans), D (pulses), E (both) | |
| `[Feeds]`: a value every tick while a clip is active | A, C, E (poison) | |
| `[Pulse]` on clip start and clip end | D, E | buffs expire through the end pulse |
| `[Pulse]` on a marker (`IMarker<T>`) | D (CLI JSON only) | **gap**: Unity's Timeline window has no Tl marker yet |
| Game pulses from code (`KindPulses.To`) | G | damage sent from a gameplay job |
| `ITlBaker`: components, buffers, blobs a clip needs | A, G | |
| Overlapping clips on one track (first wins in `ITlTrack`) | C | |
| `IBlend` (authored blend factor between overlapping clips) | none | plain-host only; `ITlTrack` has no blend. **gap** in Unity authoring |
| Start, Stop, Pause, Resume from code | B, G | |
| StopChannel, StopAll | B | needs two small `TlRequests` helpers (section 4) |
| Start at a tick (seek) | B, H | seek is Stop + Start(at) today; a real `Seek` op is missing |
| When it ends: Stop, Loop, Hold, Play next | B (one timeline of each), C (Loop) | |
| Channels: replace on the same channel, stack without one | B, E (buff refresh) | |
| Exact rewind (whole world backward) | B, D, E, F | |
| Rewind one timeline while others play forward | none | **gap**: `PlaybackFlags.Reverse` exists but no request sets it |
| Speed | B (whole world, `Time.timeScale`) | **gap**: no speed per playback |
| Presentation events `Events<T>` -> `TimelineEvent<T>` | G | |
| Play On Awake, Initial Time, Also Plays | A, B, H | |
| Live edit in Play (value changes, time kept) | A, C | |
| Edit-mode scrub preview (Timeline playhead moves the object) | A, C | preview writes only the Transform; C's colours are not previewed |
| Plain .NET host (`TimelineWorld`, `PlaybackTable`, `KindStore`) | D, E, F, H | |
| `tl run` CLI with no engine | D, E, F, H | EA |
| Same data, same hash in both hosts | F (also checked by D, E, H) | |
| Determinism (same hash twice) | D, E, F, H | |
| Many entities, 0 B per warm frame, ns/entity row | H | |
| Kind Report, inline capacity | H | |
| IL2CPP and CoreCLR players | A (exists, x 4.0333), F (player prints the F hash) | |
| Slot keys (`[TimelineSlotKeys]`, `TimelineSlot` loadouts) | none | **gap**: no Unity authoring for slot tables |
| Library hot reload in the plain host (`TimelineWorld.Reload`) | none | could be `tl run --watch` later |
| Tick rate (`Project Settings > Tl`) | A README | |
| Tl Monitor window, Tl Trigger, `GameObject > Tl > Timeline Object` | none | planned in zero-config-ux.md, **not built**; B's panel covers the runtime half of Monitor |
| Bindings to another object from a clip | none | not supported; G does it in gameplay code with a dragged `Target` field |
| Several SubScenes sharing timelines, streaming | none | tested only |

Features with no demo after this plan: `TimelineId` clip fields, `IBlend`, per-playback reverse and speed, slot keys, library hot reload in
the plain host, Monitor/Trigger, bindings, multi-SubScene streaming. The gaps marked **gap** are missing in Tl, not just missing a demo.

---

## 2. The demos

Common to every Unity demo:
- One Package Manager sample per demo. Import opens its scene (the builder runs once, like the Force demo). Menu `Tl > Samples > Build <Name>`
  rebuilds it.
- Scene: camera framing the action, light, ground, a SubScene with the objects, a small UI Toolkit text overlay that says what is playing.
- Each sample has its own asmdef (`Tl.Demo.<Name>`) so all samples can be imported together without type clashes. The Force demo keeps no
  asmdef on purpose (it is the five-file contract).
- Each sample has a PlayMode test that prints one line `TL-DEMO <name> ...` with its numbers. Test asmdefs use `UNITY_INCLUDE_TESTS`, so
  they vanish in projects without the test framework.

Code below is the target API. Names that exist on main today are used as they are; anything new is marked `(new)` and listed in section 4.
Pack code is plain C# (no Unity types) so it compiles in Unity and in .NET.

### A. Force (the existing contract, polished)

**Player sees:** a blue cube on a grid floor with 1 m lines and numbers. It slides right, fast then slowing, stops near 4 m, waits 1 s,
is pushed back the same way and repeats. The camera follows the cube smoothly. Top left: `Push  1.2 / 2.0 s  force +3.1 X  x 2.84 m`.

**Designer authors:** `Push.playable`: one Force track, one 2 s clip (Direction 1,0,0, Strength 4, falling Falloff) and a 1 s empty gap after it,
When it ends: Play next `Return`. `Return` is the same with Direction -1,0,0 and Play next `Push`, so the cube goes there and back forever.

**Coder writes:** the five Force files, unchanged (USING-ECS.md section 2). Additions only in the sample's own `Presentation/` folder:

```csharp
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Tl;

public struct FollowCamera : IComponentData { public float3 Offset; public float Sharpness; }
public struct FollowTarget : IComponentData { }

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
```

The camera itself stays a normal GameObject; a 10-line `CameraFromEntity` MonoBehaviour copies one entity's `LocalToWorld` to it in
`LateUpdate` (read only, through an `EntityQuery` on `FollowCamera`, no names). The overlay reads `Force` and the cube's `Playback` the same
way as B's panel (section B, `PanelRow`).

Also fixes issue #24 before polishing (the `_TlMenu` invalid-track warnings and the red "track has 0 clips" error), because the check doc
promises a silent console.

**README outline:** 1) what you see; 2) the five files, one paragraph each; 3) open the Timeline window and change Strength in Play;
4) scrub the playhead without Play; 5) where to go next (B, C).

**First 10 minutes:** import, press Play, watch it loop. Open `Push` in the Timeline window, raise Strength to 10 in Play: the next lap goes
further. Drag the playhead in Edit mode: the cube follows. Read `ForceKind.cs`, 16 lines.

**Acceptance (headless):** existing `ForceDemoTests` stays: one lap ends at x **4.0333**. New: a PlayMode picture test renders the camera
at the end of the first push and finds the cube's screen centre within 2% of the predicted pixel (cube at x 4.0333, camera follow settled).
Console during build, import and Play: **0 warnings, 0 errors** (issue #24 fixed). Line:
`TL-DEMO force x=4.0333 warnings=0 errors=0 picture=ok`.

### B. Playback panel (the control demo the owner asked for)

**Player sees:** a capsule on a floor and a panel on the right:

```
Timelines                         state     time        channel
Patrol      [Play][Pause][Resume][Stop]   playing   1.2/4.0 s   Body
Jump        [Play][Pause][Resume][Stop]   -                     Body
Wave        [Play][Pause][Resume][Stop]   paused    0.5/1.0 s   Overlay
Spin        [Play][Pause][Resume][Stop]   playing   0.3/2.0 s   (none)
Seek Patrol  [-----o-------------]
Channels    [Stop Body] [Stop Overlay] [Stop all]
Speed       [0.25] [0.5] [1] [2] [4]
Direction   [Forward] [Rewind]
```

Starting Jump replaces Patrol (same channel Body) and Jump hands back to Patrol when it ends (Play next). Wave (Overlay) and Spin (no
channel) play on top of either. Rewind plays every timeline backward, exactly, until Forward is pressed. Keys: 1-4 start, P pause all.

**Designer authors** (with C's Motion tracks, section C):
- `Patrol.playable`: Move track A->B->A, 4 s, Channel Body, When it ends Loop.
- `Jump.playable`: Move track up and down, 1 s, Channel Body, Play next Patrol.
- `Wave.playable`: Scale track 1->1.3->1, Tint track, 1 s, Channel Overlay, Hold.
- `Spin.playable`: Spin track 0->360, 2 s, no channel, Stop.
- Capsule in the SubScene: Playable Director (Patrol, Play On Awake), and `Panel Target` with the four timelines dragged into its list.
- Main scene: a UI Document (PanelSettings dragged in) with the `Playback Panel` component.

**Coder writes** (three files):

`PanelData.cs`
```csharp
using Tl;
using Unity.Collections;
using Unity.Entities;

public struct PanelTarget : IComponentData { }

public struct PanelTimeline : IBufferElementData
{
    public TlTimeline Timeline;
    public FixedString64Bytes Name;
    public ushort Ticks;
}

public enum PanelOp : byte { Play, Pause, Resume, Stop, Seek, StopChannel, StopAll, Rewind, Forward }

public struct PanelCommand : IBufferElementData
{
    public PanelOp Op;
    public byte Index;
    public ushort Value;
}

public struct PanelRow : IBufferElementData
{
    public byte Index;
    public ushort Tick;
    public ushort Ticks;
    public byte Channel;
    public PlaybackFlags Flags;
}
```

`PanelTargetAuthoring.cs` (in the SubScene; the designer drags timelines into the list)
```csharp
using Tl;
using Tl.Unity;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

public sealed class PanelTargetAuthoring : MonoBehaviour
{
    public TimelineAsset[] Timelines;
}

public sealed class PanelTargetBaker : Baker<PanelTargetAuthoring>
{
    public override void Bake(PanelTargetAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent<PanelTarget>(entity);
        AddBuffer<PanelCommand>(entity);
        AddBuffer<PanelRow>(entity);
        var rows = AddBuffer<PanelTimeline>(entity);
        foreach (var timeline in authoring.Timelines)
        {
            if (timeline == null)
                continue;
            rows.Add(new PanelTimeline
            {
                Timeline = TlBaking.GetTimeline(this, timeline),
                Name = timeline.name,
                Ticks = TlBaking.Ticks(timeline.duration)
            });
        }
    }
}
```

`PanelSystems.cs` (Burst, jobs)
```csharp
using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderFirst = true)]
public partial struct PanelCommandSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PanelTarget>();
        state.RequireForUpdate<TimelineStep>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new ApplyPanelCommands
        {
            Steps = SystemAPI.GetComponentLookup<TimelineStep>(),
            StepEntity = SystemAPI.GetSingletonEntity<TimelineStep>()
        }.Schedule();
    }
}

[BurstCompile]
[WithAll(typeof(PanelTarget))]
public partial struct ApplyPanelCommands : IJobEntity
{
    public ComponentLookup<TimelineStep> Steps;
    public Entity StepEntity;

    void Execute(DynamicBuffer<PanelCommand> commands, in DynamicBuffer<PanelTimeline> timelines, DynamicBuffer<TimelineRequest> requests)
    {
        for (var i = 0; i < commands.Length; i++)
        {
            var c = commands[i];
            var t = c.Index < timelines.Length ? timelines[c.Index].Timeline : default;
            switch (c.Op)
            {
                case PanelOp.Play: requests.Start(t); break;
                case PanelOp.Pause: requests.Pause(t); break;
                case PanelOp.Resume: requests.Resume(t); break;
                case PanelOp.Stop: requests.Stop(t); break;
                case PanelOp.Seek: requests.Stop(t); requests.Start(t, c.Value); break;
                case PanelOp.StopChannel: requests.StopChannel((byte)c.Value); break;
                case PanelOp.StopAll: requests.StopAll(); break;
                case PanelOp.Rewind: Steps.GetRefRW(StepEntity).ValueRW.Forward = false; break;
                case PanelOp.Forward: Steps.GetRefRW(StepEntity).ValueRW.Forward = true; break;
            }
        }
        commands.Clear();
    }
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct PanelStatusSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<PanelTarget>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state) => new WritePanelRows().ScheduleParallel();
}

[BurstCompile]
[WithAll(typeof(PanelTarget))]
public partial struct WritePanelRows : IJobEntity
{
    void Execute(DynamicBuffer<PanelRow> rows, in DynamicBuffer<Playback> playing, in DynamicBuffer<TlTimelineSet> set,
        in DynamicBuffer<PanelTimeline> timelines)
    {
        rows.Clear();
        for (var p = 0; p < playing.Length; p++)
        {
            var asset = default(Unity.Entities.Hash128);
            for (var s = 0; s < set.Length; s++)
                if (set[s].Id == playing[p].Timeline)
                    asset = set[s].Blob.Value.Asset;
            for (var k = 0; k < timelines.Length; k++)
                if (timelines[k].Timeline.Asset == asset)
                    rows.Add(new PanelRow
                    {
                        Index = (byte)k, Tick = playing[p].Tick, Ticks = timelines[k].Ticks,
                        Channel = playing[p].Channel, Flags = playing[p].Flags
                    });
        }
    }
}
```

`PlaybackPanel.cs` (the UI; managed, runs only on clicks and once per frame for labels)
```csharp
using Tl;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class PlaybackPanel : MonoBehaviour
{
    public UIDocument Document;
    EntityQuery _targets;
    Label[] _state;
    Slider _seek;

    void OnEnable()
    {
        _targets = World.DefaultGameObjectInjectionWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PanelTarget>());
    }

    void Update()
    {
        if (_targets.IsEmpty)
            return;
        var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
        var target = _targets.GetSingletonEntity();
        var timelines = manager.GetBuffer<PanelTimeline>(target, true);
        if (_state == null)
            Build(timelines);
        for (var i = 0; i < _state.Length; i++)
            _state[i].text = "-";
        foreach (var row in manager.GetBuffer<PanelRow>(target, true))
        {
            var paused = (row.Flags & PlaybackFlags.Paused) != 0;
            _state[row.Index].text = $"{(paused ? "paused" : "playing")}  {row.Tick / 60f:0.0}/{row.Ticks / 60f:0.0} s  ch {row.Channel}";
        }
    }

    void Build(DynamicBuffer<PanelTimeline> timelines)
    {
        var root = Document.rootVisualElement;
        root.Clear();
        _state = new Label[timelines.Length];
        for (var i = 0; i < timelines.Length; i++)
        {
            var index = (byte)i;
            var line = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            line.Add(new Label(timelines[i].Name.ToString()));
            line.Add(new Button(() => Send(PanelOp.Play, index, 0)) { text = "Play" });
            line.Add(new Button(() => Send(PanelOp.Pause, index, 0)) { text = "Pause" });
            line.Add(new Button(() => Send(PanelOp.Resume, index, 0)) { text = "Resume" });
            line.Add(new Button(() => Send(PanelOp.Stop, index, 0)) { text = "Stop" });
            line.Add(_state[i] = new Label("-"));
            root.Add(line);
        }
        _seek = new Slider("Seek first", 0, timelines[0].Ticks - 1);
        _seek.RegisterValueChangedCallback(e => Send(PanelOp.Seek, 0, (ushort)e.newValue));
        root.Add(_seek);
        for (byte channel = 1; channel <= 2; channel++)
        {
            var c = channel;
            root.Add(new Button(() => Send(PanelOp.StopChannel, 0, c)) { text = $"Stop channel {c}" });
        }
        root.Add(new Button(() => Send(PanelOp.StopAll, 0, 0)) { text = "Stop all" });
        foreach (var speed in new[] { 0.25f, 0.5f, 1f, 2f, 4f })
            root.Add(new Button(() => Time.timeScale = speed) { text = $"{speed}x" });
        root.Add(new Button(() => Send(PanelOp.Rewind, 0, 0)) { text = "Rewind" });
        root.Add(new Button(() => Send(PanelOp.Forward, 0, 0)) { text = "Forward" });
    }

    void Send(PanelOp op, byte index, ushort value)
    {
        if (_targets.IsEmpty)
            return;
        var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
        manager.GetBuffer<PanelCommand>(_targets.GetSingletonEntity()).Add(new PanelCommand { Op = op, Index = index, Value = value });
    }
}
```

Honest limits shown in the README: speed and rewind act on the whole world (all timelines), because Tl has no per-playback speed or
direction request yet; seek restarts the playback at the tick (its clips get a synthetic start). Channel names are shown as numbers until
the package exposes the names to bakers.

**README outline:** 1) what each button calls (`TlRequests.Start/Pause/Resume/Stop/StopChannel/StopAll`, `TimelineStep.Forward`,
`Time.timeScale`); 2) channels and Play next, with the four timeline settings in a table; 3) the UI-to-ECS boundary: one command buffer
in, one status buffer out, everything else Burst; 4) limits; 5) how to drive the same requests from your own gameplay job (link G).

**First 10 minutes:** press Play, click Jump (Patrol is replaced, then comes back), click Wave and Spin (they stack), pause Wave, drag the
seek bar, press Rewind and watch everything run backward, try 4x. Open `PanelSystems.cs`: one switch maps every button to one request.

**Acceptance (headless):** PlayMode test drives the panel by appending `PanelCommand`s from the test (no UI), at fixed steps:
step 0 Patrol plays; 60 Play Jump; 120 Play Wave; 130 Pause Wave; 160 Play Spin; 200 Seek Patrol 30; 240 Resume Wave; 300 Rewind;
360 Forward; 420 StopChannel 1; 480 StopAll. After each command it records the `PanelRow`s; at the end it hashes all rows and the
capsule's `Pose` per step (FNV-1a 64, section 3). Pins: Jump replaced Patrol at step 60 and Patrol is back at step 120 (Jump is 60 ticks);
Wave's tick is equal at steps 131 and 239 (paused); after Rewind from 300 to 360 Patrol's tick equals its tick at step 240, while Spin
(ended at 280) and Wave (held since 290) do not come back, because rewind replays only live playbacks (the README says so); after StopAll
no rows. Line: `TL-DEMO panel rows=ok paused=ok rewind=exact hash=<H>`; H pinned on the first green run, must be equal on two runs.
A second test sets `Time.timeScale = 4` for 1 s of real time and checks about 240 ticks passed (±10%).

### C. Animation (values over time on transforms and colours, through a kind pack)

**Player sees:** three objects in a row. A cube eases from left to right and back. A sphere breathes (scale) and changes colour red ->
yellow -> red. A capsule spins while hopping; halfway an overriding Move clip with higher Priority pulls it sideways, then lets go.
Overlay: each object's timeline and time.

**Designer authors:** only Motion pack tracks (in the `+` menu under `Motion/`): `Move` (From, To, Ease curve, Priority), `Scale`
(From, To, Ease), `Spin` (From yaw, To yaw, Ease), `Tint` (From colour, To colour, Ease). Three timelines `Glide`, `Breathe`, `Hop`, all Loop.
No code.

**Coder writes:** nothing for the demo. The pack is the code a coder would write for their own tracks. Portable pack (in tl, plain C#,
compiles in .NET and in Unity):

`Tl.Pack.Motion/Motion.cs`
```csharp
using Tl;

namespace Tl.Pack.Motion
{
    public struct Vec3
    {
        public float X, Y, Z;
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new Vec3 { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t, Z = a.Z + (b.Z - a.Z) * t };
    }

    public struct Rgba
    {
        public float R, G, B, A;
        public static Rgba Lerp(Rgba a, Rgba b, float t) => new Rgba { R = a.R + (b.R - a.R) * t, G = a.G + (b.G - a.G) * t, B = a.B + (b.B - a.B) * t, A = a.A + (b.A - a.A) * t };
    }

    public partial struct Pose
    {
        public Vec3 Position;
        public Vec3 Scale;
        public float Yaw;
        public byte Has;
    }

    public partial struct Tint
    {
        public Rgba Color;
        public byte Has;
    }

    public struct Placed { public Vec3 Value; public int Priority; }

    [TlClip("Motion/Move")] public struct MoveClip { public Vec3 From; public Vec3 To; public TlCurve Ease; public int Priority; }
    [TlClip("Motion/Scale")] public struct ScaleClip { public Vec3 From; public Vec3 To; public TlCurve Ease; }
    [TlClip("Motion/Spin")] public struct SpinClip { public float FromYaw; public float ToYaw; public TlCurve Ease; }
    [TlClip("Motion/Tint")] public struct TintClip { public Rgba From; public Rgba To; public TlCurve Ease; }

    [TlKind]
    public struct MotionPosition
    {
        public static void Combine(ref Placed total, in Placed next) { if (next.Priority >= total.Priority) total = next; }
        public static void Apply(in Placed total, ref Pose pose) { pose.Position = total.Value; pose.Has |= 1; }
    }

    [TlKind]
    public struct MotionScale
    {
        public static void Combine(ref Vec3 total, in Vec3 next) => total = next;
        public static void Apply(in Vec3 total, ref Pose pose) { pose.Scale = total; pose.Has |= 2; }
    }

    [TlKind]
    public struct MotionYaw
    {
        public static void Combine(ref float total, in float next) => total = next;
        public static void Apply(in float total, ref Pose pose) { pose.Yaw = total; pose.Has |= 4; }
    }

    [TlKind]
    public struct MotionTint
    {
        public static void Combine(ref Rgba total, in Rgba next) => total = next;
        public static void Apply(in Rgba total, ref Tint tint) { tint.Color = total; tint.Has = 1; }
    }

    public struct MoveTrack : ITlTrack<MoveClip>
    {
        [Feeds(typeof(MotionPosition))]
        public static Placed Evaluate(in TlFrame<MoveClip> f)
            => new Placed { Value = Vec3.Lerp(f.Clip.From, f.Clip.To, f.Clip.Ease.Evaluate(f.Progress)), Priority = f.Clip.Priority };
    }

    public struct ScaleTrack : ITlTrack<ScaleClip>
    {
        [Feeds(typeof(MotionScale))]
        public static Vec3 Evaluate(in TlFrame<ScaleClip> f) => Vec3.Lerp(f.Clip.From, f.Clip.To, f.Clip.Ease.Evaluate(f.Progress));
    }

    public struct SpinTrack : ITlTrack<SpinClip>
    {
        [Feeds(typeof(MotionYaw))]
        public static float Evaluate(in TlFrame<SpinClip> f) => f.Clip.FromYaw + (f.Clip.ToYaw - f.Clip.FromYaw) * f.Clip.Ease.Evaluate(f.Progress);
    }

    public struct TintTrack : ITlTrack<TintClip>
    {
        [Feeds(typeof(MotionTint))]
        public static Rgba Evaluate(in TlFrame<TintClip> f) => Rgba.Lerp(f.Clip.From, f.Clip.To, f.Clip.Ease.Evaluate(f.Progress));
    }
}
```

Values are a pure function of the tick, so rewind and seek are exact and the CLI gives the same numbers. `Has` bits keep the object's own
transform until a track first writes it (no "scale 0 before the first clip").

Unity adapter for the pack (in `com.tl.unity`, the only Unity-specific code; one `partial` per pack type makes it a component):
```csharp
using Tl;
using Tl.Pack.Motion;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

namespace Tl.Pack.Motion
{
    public partial struct Pose : IComponentData { }
    public partial struct Tint : IComponentData { }
}

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct MotionToTransformSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new PoseToTransform().ScheduleParallel();
        new TintToColor().ScheduleParallel();
    }
}

[BurstCompile]
public partial struct PoseToTransform : IJobEntity
{
    void Execute(ref LocalTransform transform, ref PostTransformMatrix scale, in Pose pose)
    {
        if ((pose.Has & 1) != 0) transform.Position = new float3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        if ((pose.Has & 4) != 0) transform.Rotation = quaternion.RotateY(math.radians(pose.Yaw));
        if ((pose.Has & 2) != 0) scale.Value = float4x4.Scale(pose.Scale.X, pose.Scale.Y, pose.Scale.Z);
    }
}

[BurstCompile]
public partial struct TintToColor : IJobEntity
{
    void Execute(ref URPMaterialPropertyBaseColor color, in Tint tint)
    {
        if (tint.Has != 0) color.Value = new float4(tint.Color.R, tint.Color.G, tint.Color.B, tint.Color.A);
    }
}
```

The pack's `ITlBaker`s (in the adapter) add `PostTransformMatrix` and `URPMaterialPropertyBaseColor` when an object plays a Scale or Tint clip,
so the designer adds nothing.

**README outline:** 1) the four tracks and their fields; 2) override with Priority (the capsule); 3) how a pack is built: clip, kind, track,
nothing else; 4) how the Unity adapter maps `Pose`/`Tint` to Transform and URP colour; 5) run the same timelines in the CLI (link F).

**First 10 minutes:** press Play. Open `Breathe`, change the Tint To colour in Play: the sphere changes within a second. Scrub `Glide` in
Edit mode: the cube moves (colours are not previewed, the README says so). Add a Spin track to `Glide`: the cube spins too.

**Acceptance (headless):** PlayMode test records each object's `Pose` and `Tint` at ticks 30, 60, 90, 120 and hashes every step for 600
steps. The Edit-mode preview test reads the cube's position at ticks 30, 60, 90 and must equal Play bit for bit (as the Force preview test
does). The CLI runs the exported `Glide/Breathe/Hop` JSON through `tl run` and must print the same per-step hash. Line:
`TL-DEMO animation hash=<H> preview=equal cli=equal`.

### D. Inventory over time (CLI only, no engine)

**Player sees:** console output, one line per change, then a check:

```
$ tl run demos/inventory
tick   0  +5 Wood                 Wood 5
tick  60  +3 Stone                Wood 5  Stone 3
tick 120  +2 Potion               Wood 5  Stone 3  Potion 2
tick 180  -4 Wood (craft starts)  Wood 1  Stone 3  Potion 2
tick 299  +2 Plank (craft ends)   Wood 1  Stone 3  Plank 2  Potion 2
tick 360  -1 Potion               ... Potion 1
tick 420  +10 Coin (pickup)       ... Coin 10
step 450: rewind 450 steps        bag empty: yes
tick 480  +1 Gem (pickup)
tick 540  +1 Sword
final: Wood 1 Stone 3 Plank 2 Potion 1 Sword 1 Coin 10 Key 0 Gem 1
state hash 0x................
```

**Designer authors** (JSON in `demos/inventory/`; in Unity the same timeline can be built in the Timeline window and exported):
`loot_run.json`, 600 ticks, three tracks (`Give`, `Take`, `Craft`) and two `Pickup` markers; `script.json`: start loot_run at step 0, run
450 steps, rewind 450, then run 600 forward.

```json
{"name":"loot_run","duration":600,"tracks":[
 {"name":"give","type":"Tl.Pack.Inventory.GiveTrack","clips":[
  {"type":"Tl.Pack.Inventory.GiveClip","start":0,"end":1,"data":{"Item":"Wood","Count":5}},
  {"type":"Tl.Pack.Inventory.GiveClip","start":60,"end":61,"data":{"Item":"Stone","Count":3}},
  {"type":"Tl.Pack.Inventory.GiveClip","start":120,"end":121,"data":{"Item":"Potion","Count":2}},
  {"type":"Tl.Pack.Inventory.GiveClip","start":540,"end":541,"data":{"Item":"Sword","Count":1}}]},
 {"name":"take","type":"Tl.Pack.Inventory.GiveTrack","clips":[
  {"type":"Tl.Pack.Inventory.GiveClip","start":360,"end":361,"data":{"Item":"Potion","Count":-1}}]},
 {"name":"craft","type":"Tl.Pack.Inventory.CraftTrack","clips":[
  {"type":"Tl.Pack.Inventory.CraftClip","start":180,"end":300,"data":{"Input":"Wood","InputCount":4,"Output":"Plank","OutputCount":2}}]}],
 "markers":[
  {"type":"Tl.Pack.Inventory.PickupMarker","tick":420,"data":{"Item":"Coin","Count":10}},
  {"type":"Tl.Pack.Inventory.PickupMarker","tick":480,"data":{"Item":"Gem","Count":1}}]}
```

(Exact JSON spelling follows the EA format; type names here use the namespace-qualified form.)

**Coder writes:** the pack (plain C#):

`Tl.Pack.Inventory/Inventory.cs`
```csharp
using Tl;

namespace Tl.Pack.Inventory
{
    public enum Item : byte { Wood, Stone, Plank, Potion, Sword, Coin, Key, Gem }

    public partial struct Bag
    {
        public int Wood, Stone, Plank, Potion, Sword, Coin, Key, Gem;

        public void Add(Item item, int count)
        {
            switch (item)
            {
                case Item.Wood: Wood += count; break;
                case Item.Stone: Stone += count; break;
                case Item.Plank: Plank += count; break;
                case Item.Potion: Potion += count; break;
                case Item.Sword: Sword += count; break;
                case Item.Coin: Coin += count; break;
                case Item.Key: Key += count; break;
                case Item.Gem: Gem += count; break;
            }
        }
    }

    public struct ItemDelta { public Item Item; public int Count; }

    [TlClip("Inventory/Give")] public struct GiveClip { public Item Item; public int Count; }
    [TlClip("Inventory/Craft")] public struct CraftClip { public Item Input; public int InputCount; public Item Output; public int OutputCount; }
    public struct PickupMarker { public Item Item; public int Count; }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct InventoryItems
    {
        public static void Apply(in ItemDelta delta, ref Bag bag) => bag.Add(delta.Item, delta.Count);
    }

    public struct GiveTrack : ITlTrack<GiveClip>
    {
        [Pulse(typeof(InventoryItems), On = ClipEvent.Start)]
        public static ItemDelta Give(in TlFrame<GiveClip> f) => new ItemDelta { Item = f.Clip.Item, Count = f.Direction * f.Clip.Count };
    }

    public struct CraftTrack : ITlTrack<CraftClip>
    {
        [Pulse(typeof(InventoryItems), On = ClipEvent.Start)]
        public static ItemDelta Consume(in TlFrame<CraftClip> f) => new ItemDelta { Item = f.Clip.Input, Count = -f.Direction * f.Clip.InputCount };

        [Pulse(typeof(InventoryItems), On = ClipEvent.End)]
        public static ItemDelta Produce(in TlFrame<CraftClip> f) => new ItemDelta { Item = f.Clip.Output, Count = f.Direction * f.Clip.OutputCount };
    }

    public struct Pickup : IMarker<PickupMarker>
    {
        [Pulse(typeof(InventoryItems), On = ClipEvent.Marker)]
        public static ItemDelta OnPickup(in MarkerFrame<PickupMarker> m) => new ItemDelta { Item = m.Marker.Item, Count = m.Direction * m.Marker.Count };
    }
}
```

Two ways to run it. Zero code: `tl run demos/inventory` (EA: bakes the JSON in process, loads the pack dll named in `library.json`, runs
`script.json`, prints changed values and the hash). Or embed it, the code a server or tool developer writes:

`demos/inventory/host/Program.cs`
```csharp
using Tl;
using Tl.Pack.Inventory;

var library = TlJson.BakeDirectory("demos/inventory", typeof(Bag).Assembly);
using var world = new TimelineWorld(library);
using var players = new PlaybackTable(entityCapacity: 1);
using var kinds = new KindStore(world, players);
var bags = new Bag[1];
players.Start(0, world.IdOf("loot_run"));
for (var step = 0; step < 450; step++)
    Step(true);
for (var step = 0; step < 450; step++)
    Step(false);
System.Console.WriteLine(bags[0].Equals(default(Bag)) ? "bag empty: yes" : "bag empty: NO");
for (var step = 0; step < 600; step++)
    Step(true);
System.Console.WriteLine($"final: Wood {bags[0].Wood} Stone {bags[0].Stone} Plank {bags[0].Plank} Potion {bags[0].Potion} Sword {bags[0].Sword} Coin {bags[0].Coin} Key {bags[0].Key} Gem {bags[0].Gem}");

void Step(bool forward)
{
    world.Tick(players, forward);
    new InventorySystem { Bag = bags }.Run(world, players, forward);
}

[TimelineSystem]
public ref partial struct InventorySystem
{
    public System.Span<Bag> Bag;
}
```

`TlJson.BakeDirectory` and `world.IdOf` are EA names (today: `tlb library` writes `game.tlib` and id constants).

**README outline:** 1) run it (one command); 2) read the JSON; 3) the pack, clip by clip; 4) embed it (Program.cs); 5) why rewind is exact
(every pulse is signed by `Direction`).

**First 10 minutes:** clone tl, `dotnet run --project tools/Tl.Cli -- run demos/inventory`. Edit `loot_run.json`, give 3 Keys at tick 500,
run again. Add a `Take` of 2 Stone and watch the hash change.

**Acceptance (headless):** `dotnet test demos/Demos.Tests --filter Inventory` checks: bag empty after the rewind = **yes**; final bag
**Wood 1, Stone 3, Plank 2, Potion 1, Sword 1, Coin 10, Key 0, Gem 1**; the hash equal on two runs and equal to the pinned value in
`demos/inventory/expected.txt`. The same test runs the JSON through the Unity host (F) and must print the same hash.

### E. Stats and buffs over time (Unity scene and CLI)

**Player sees (Unity):** a hero capsule with a health bar and an attack number above it, a dummy that swings at it. Hits knock the bar
down, poison ticks it in small steps (green numbers), a heal bumps it up. A Haste buff makes the hero glow and shows `ATK 20`; it expires
and the number drops to 10. Casting Haste again while it runs refreshes it (it lasts longer, does not stack). CLI shows the same as text.

**Designer authors** (Stats pack tracks, `Stats/` in the `+` menu):
- `Duel.playable` (600 ticks, Stop): Hit track (Amount 12 at 1 s, 2 s, 3 s), Poison track (4 s to 8 s, Amount 1 every 20 ticks), Heal
  track (Amount 20 at 8.33 s).
- `Haste.playable` (300 ticks, Channel Haste, Stop): Buff track, Attack +10, whole length.
- Hero: Playable Director (Duel, Play On Awake), Also Plays Haste, `Stats` starting values (Health 100, Attack 10) from the pack's
  authoring component. A keyboard key (H) casts Haste (through B's command pattern, one line in the sample's input system).

**Coder writes:** the pack (plain C#):

`Tl.Pack.Stats/Stats.cs`
```csharp
using Tl;

namespace Tl.Pack.Stats
{
    public partial struct Health { public int Value; public int Max; }
    public partial struct Attack { public int Base; public int Bonus; public int Value => Base + Bonus; }

    [TlClip("Stats/Hit")] public struct HitClip { public int Amount; }
    [TlClip("Stats/Heal")] public struct HealClip { public int Amount; }
    [TlClip("Stats/Poison")] public struct PoisonClip { public int Amount; public int Every; }
    [TlClip("Stats/Buff")] public struct BuffClip { public int AttackBonus; }

    [TlKind(Accepts = KindEntries.Both, GamePulses = 8)]
    public struct StatsDamage
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int total, ref Health health) => health.Value -= total;
    }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct StatsAttackBonus
    {
        public static void Apply(in int delta, ref Attack attack) => attack.Bonus += delta;
    }

    public struct HitTrack : ITlTrack<HitClip>
    {
        [Pulse(typeof(StatsDamage), On = ClipEvent.Start)]
        public static int Hit(in TlFrame<HitClip> f) => f.Direction * f.Clip.Amount;
    }

    public struct HealTrack : ITlTrack<HealClip>
    {
        [Pulse(typeof(StatsDamage), On = ClipEvent.Start)]
        public static int Heal(in TlFrame<HealClip> f) => -f.Direction * f.Clip.Amount;
    }

    public struct PoisonTrack : ITlTrack<PoisonClip>
    {
        [Feeds(typeof(StatsDamage))]
        public static int Tick(in TlFrame<PoisonClip> f) => f.WithinClip % f.Clip.Every == 0 ? f.Direction * f.Clip.Amount : 0;
    }

    public struct BuffTrack : ITlTrack<BuffClip>
    {
        [Pulse(typeof(StatsAttackBonus), On = ClipEvent.Start)]
        public static int Gain(in TlFrame<BuffClip> f) => f.Direction * f.Clip.AttackBonus;

        [Pulse(typeof(StatsAttackBonus), On = ClipEvent.End)]
        public static int Lose(in TlFrame<BuffClip> f) => -f.Direction * f.Clip.AttackBonus;
    }
}
```

Health is not clamped in `Apply` (a clamp cannot be undone, so rewind would drift); the bar clamps when it draws. A buff ends through its
end pulse, which also fires when the buff is stopped or replaced (`Interrupted`), so refresh on a channel keeps Attack at +10, never +20.

Unity part of the sample (presentation only):
```csharp
using Tl.Pack.Stats;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public struct HealthBar : IComponentData { public Entity Owner; public float Width; }

[BurstCompile]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct HealthBarSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new DrawHealthBars { Health = SystemAPI.GetComponentLookup<Health>(true) }.ScheduleParallel();
    }
}

[BurstCompile]
public partial struct DrawHealthBars : IJobEntity
{
    [Unity.Collections.ReadOnly] public ComponentLookup<Health> Health;

    void Execute(ref PostTransformMatrix scale, in HealthBar bar)
    {
        var h = Health[bar.Owner];
        var fill = math.saturate(h.Value / (float)math.max(1, h.Max));
        scale.Value = float4x4.Scale(bar.Width * fill, 0.1f, 0.1f);
    }
}
```

**README outline:** 1) the four clips; 2) why damage is a kind (hits, poison and game damage add up into one write per tick); 3) buffs as
start/end pulses, refresh through a channel; 4) the same data in the CLI (`tl run demos/stats`); 5) send damage from your own code (link G).

**First 10 minutes:** press Play, watch the bar. Press H twice: Haste refreshes, Attack stays 20. Open `Duel`, drag the Heal clip earlier.
Run `tl run demos/stats` and see the same numbers as text.

**Acceptance (headless):** CLI and Unity run the same `script.json` (start Duel at step 0, Haste at 100, Haste again at 250):

| Step | Health | Attack |
|---|---|---|
| 200 | 64 | 20 |
| 450 | 53 | 20 |
| 600 | 72 | 10 |

(Hits at ticks 60/120/180 take 36; poison hits at 240, 260 ... 460 take 12 by step 600 and 11 by step 450; heal +20 at 500; Haste
refreshed at 250 ends at 550.) Plus: same hash in both hosts and on two runs. Line: `TL-DEMO stats checkpoints=3/3 hash=<H> cli=equal`.

### F. Same data, two engines (the cross-engine proof)

**Player sees (Unity):** an `Arena` scene: a hero that walks a loop (Motion pack), takes hits and gets buffs (Stats pack) and picks up items
(Inventory pack), with an overlay showing Health, Attack, the bag and a running hash. The CLI prints the same hash at the same steps.

**Designer authors:** the source of truth is JSON in tl `demos/arena/` (`library.json`, `patrol.json`, `duel.json`, `haste.json`,
`loot_run.json`, `script.json`). The sample's builder runs `Tl > Import Timeline JSON` (EA, Unity adapter) to make real `.playable` files
with Tl tracks from that JSON, so a designer can open and edit them in the Timeline window. `Tl > Export Timeline JSON` (EA) writes them back.

**Coder writes:** nothing new: the three packs. The script runner on the Unity side is test code plus one Burst system:

```csharp
using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

public struct ScriptOp : IBufferElementData { public int Step; public TlTimeline Timeline; public byte Kind; }
public struct ScriptClock : IComponentData { public int Step; }

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderFirst = true)]
public partial struct ScriptSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new RunScript
        {
            Steps = SystemAPI.GetComponentLookup<TimelineStep>(),
            StepEntity = SystemAPI.GetSingletonEntity<TimelineStep>()
        }.Schedule();
    }
}

[BurstCompile]
public partial struct RunScript : IJobEntity
{
    public ComponentLookup<TimelineStep> Steps;
    public Entity StepEntity;

    void Execute(ref ScriptClock clock, in DynamicBuffer<ScriptOp> ops, DynamicBuffer<TimelineRequest> requests)
    {
        for (var i = 0; i < ops.Length; i++)
        {
            if (ops[i].Step != clock.Step)
                continue;
            if (ops[i].Kind == 0) requests.Start(ops[i].Timeline);
            else if (ops[i].Kind == 1) requests.Stop(ops[i].Timeline);
            else Steps.GetRefRW(StepEntity).ValueRW.Forward = ops[i].Kind == 2;
        }
        clock.Step++;
    }
}
```

**State hash** (both hosts, defined once in EA, used by D, E, F, H): FNV-1a 64. Per step, for each scripted entity in script order, the raw
little-endian bytes of every pack component it has (`Pose`, `Tint`, `Health`, `Attack`, `Bag`, in that order). Floats are hashed as bits.

**README outline:** 1) what is proved; 2) run it in .NET (`tl run demos/arena --hash`); 3) run it in Unity (open Arena, press Play, compare
the overlay hash) or headless; 4) edit a timeline in the Timeline window, export, run the CLI: still equal; 5) what breaks equality
(game code outside the packs, physics).

**First 10 minutes:** run the CLI, note the hash. Open Arena in Unity, press Play: the overlay shows the same hash at step 600. Change a Hit
amount in the Timeline window, export JSON, run the CLI: both hashes changed and are still equal.

**Acceptance (headless):** three numbers must be equal and pinned in `demos/arena/expected.txt`:
1. `tl run demos/arena --hash` (CLI);
2. Unity PlayMode test, default world, SubScene with the imported `.playable` files, `ScriptSystem` driving the script: same hash at steps
   150, 300, 600;
3. `Tl > Export Timeline JSON` of the imported assets equals the committed JSON byte for byte (round trip).
Optional fourth: an IL2CPP player of the Arena scene logs the same hash (Phase 8 pattern). D's and E's JSON go through the same Unity test
(no scene needed) and must match their CLI hashes too. Line: `TL-DEMO samedata cli=<H> unity=<H> roundtrip=equal`.

Risk: Burst and .NET float results for `Lerp` and `TlCurve.Evaluate` must be bit-equal. The pinned kind hash already crosses hosts with
floats (`Motion`), so it is expected to hold; if it does not, the test prints the first step and component that differ and that is a Tl
issue, not something to round away. Int-only demos (D, E) must be equal regardless.

### G. Events to gameplay

**Player sees:** a knight plays an attack combo on a loop. Each strike flashes a spark on a training dummy, the dummy flinches (plays its
own Hurt timeline), its health drops and a score counter goes up. Text: `Strikes 7  Dummy health 58`.

**Designer authors:** `Combo.playable` (Loop): Strike track (Arena pack clip `Strike`, Amount 6, three clips), a Motion Move track for the
lunge. `Hurt.playable` on the dummy (Scale squash, 0.3 s). Knight's `Arena Attacker` component: drag the dummy into **Target**, the spark
prefab into **Spark**, `Hurt` into **Hurt**.

**Coder writes:**

`Strike.cs`
```csharp
using Tl;
using Tl.Unity;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

[TlClip("Arena/Strike")] public struct StrikeClip { public int Amount; }
public struct Strike { public int Amount; }

public struct StrikeTrack : ITlTrack<StrikeClip>
{
    public static void OnClipStart(in TlFrame<StrikeClip> f, ref Events<Strike> strikes)
    {
        if (f.Direction > 0)
            strikes.Raise(new Strike { Amount = f.Clip.Amount });
    }
}

[TimelineSystem]
public partial struct ArenaEvents
{
    public Events<Strike> Strikes;
}

public struct Attacker : IComponentData { public Entity Target; public Entity Spark; public TlTimeline Hurt; }
public struct Score : IComponentData { public int Strikes; }

public sealed class ArenaAttackerAuthoring : MonoBehaviour
{
    public GameObject Target;
    public GameObject Spark;
    public TimelineAsset Hurt;
}

public sealed class ArenaAttackerBaker : Baker<ArenaAttackerAuthoring>
{
    public override void Bake(ArenaAttackerAuthoring a)
    {
        AddComponent(GetEntity(TransformUsageFlags.Dynamic), new Attacker
        {
            Target = GetEntity(a.Target, TransformUsageFlags.Dynamic),
            Spark = GetEntity(a.Spark, TransformUsageFlags.Dynamic),
            Hurt = TlBaking.GetTimeline(this, a.Hurt)
        });
        AddComponent(GetEntity(TransformUsageFlags.Dynamic), new Score());
    }
}
```

`StrikeSystem.cs`
```csharp
using Tl.Pack.Stats;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct StrikeSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<TimelineKindTick>();
        state.RequireForUpdate<EndFixedStepSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var tick = SystemAPI.GetSingletonRW<TimelineKindTick>();
        var sender = tick.ValueRW.BeginPulses(64);
        var ecb = SystemAPI.GetSingleton<EndFixedStepSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
        state.Dependency = new ReactToStrikes
        {
            Damage = StatsDamage.Pulses(sender.Writer),
            Requests = SystemAPI.GetBufferLookup<TimelineRequest>(),
            Positions = SystemAPI.GetComponentLookup<LocalTransform>(true),
            Commands = ecb
        }.Schedule(state.Dependency);
        tick.ValueRW.AddPulseWriter(state.Dependency);
    }
}

[BurstCompile]
public partial struct ReactToStrikes : IJobEntity
{
    public KindPulses<int> Damage;
    public BufferLookup<TimelineRequest> Requests;
    [ReadOnly] public ComponentLookup<LocalTransform> Positions;
    public EntityCommandBuffer Commands;

    void Execute(in Attacker attacker, ref Score score, in DynamicBuffer<TimelineEvent<Strike>> strikes)
    {
        for (var i = 0; i < strikes.Length; i++)
        {
            Damage.To(attacker.Target, strikes[i].Record.Value.Amount);
            Requests[attacker.Target].Start(attacker.Hurt);
            var spark = Commands.Instantiate(attacker.Spark);
            Commands.SetComponent(spark, Positions[attacker.Target]);
            score.Strikes++;
        }
    }
}
```

The dummy's health comes from the Stats pack (`StatsDamage` accepts game pulses, `GamePulses = 8`), so timeline hits and gameplay damage sum
into one write per tick. The dummy carries the pack's `Stats` starting-values component, whose baker also adds the `StatsDamage` entry
buffer; without it the pulses would be dropped (`DroppedPulses`) because none of the dummy's timelines reaches that kind. Spark cleanup is a 10-line Burst lifetime job in the sample.

**README outline:** 1) the event path: clip start -> `Events<Strike>` -> `TimelineEvent<Strike>` buffer -> your job; 2) sending damage to
another entity with game pulses; 3) starting a timeline on another entity; 4) why you read events inside `TlSimulationGroup` (one buffer per
tick); 5) what not to do (no managed callbacks, no `SystemBase`).

**First 10 minutes:** press Play, count sparks. Open `Combo`, add a fourth Strike clip: one more spark per loop. Read `StrikeSystem.cs`.

**Acceptance (headless):** PlayMode test, 600 steps (10 s): Combo loops every 120 ticks with strikes at ticks 20, 50, 80. Expected **15**
strikes, dummy health **100 - 15 x 6 = 10**, **15** Hurt starts, **15** sparks spawned. Event count per step equals the count from the CLI run
of the same JSON (events hash, like the existing `6607956939547305625` check). Line: `TL-DEMO events strikes=15 health=10 hurt=15 sparks=15`.

### H. Crowd (many entities, a bench row)

**Player sees:** a field of 10,000 small capsules dancing (Motion pack: hop, spin, colour wave), each starting at a different point in the
same timeline so the field ripples. Overlay: entity count, ms per tick for Tl, `0 B` allocated per frame. Keys: 1k / 10k / 100k.

**Designer authors:** `Dance.playable` (Loop, 2 s: Move hop, Spin, Tint), a `Dancer` prefab with a Playable Director on Dance (Play On
Awake off), and a `Crowd` object: drag Dancer into **Prefab**, set **Count** and **Spacing**.

**Coder writes:**
```csharp
using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Timeline;

public struct Crowd : IComponentData { public Entity Prefab; public int Count; public float Spacing; public TlTimeline Dance; public ushort Ticks; }
public struct Dancer : IComponentData { }

public sealed class CrowdAuthoring : MonoBehaviour
{
    public GameObject Prefab;
    public int Count = 10000;
    public float Spacing = 1.5f;
    public TimelineAsset Dance;
}

public sealed class CrowdBaker : Baker<CrowdAuthoring>
{
    public override void Bake(CrowdAuthoring a)
    {
        AddComponent(GetEntity(TransformUsageFlags.None), new Crowd
        {
            Prefab = GetEntity(a.Prefab, TransformUsageFlags.Dynamic),
            Count = a.Count,
            Spacing = a.Spacing,
            Dance = TlBaking.GetTimeline(this, a.Dance),
            Ticks = TlBaking.Ticks(a.Dance.duration)
        });
    }
}

[BurstCompile]
public partial struct CrowdSpawnSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<Crowd>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var crowd = SystemAPI.GetSingleton<Crowd>();
        var dancers = state.EntityManager.Instantiate(crowd.Prefab, crowd.Count, Allocator.Temp);
        state.EntityManager.AddComponent<Dancer>(dancers);
        state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<Crowd>());
        state.Dependency = new PlaceDancers { Crowd = crowd, Side = (int)math.ceil(math.sqrt(crowd.Count)) }.ScheduleParallel(state.Dependency);
    }
}

[BurstCompile]
[WithAll(typeof(Dancer))]
public partial struct PlaceDancers : IJobEntity
{
    public Crowd Crowd;
    public int Side;

    void Execute([EntityIndexInQuery] int i, ref LocalTransform transform, DynamicBuffer<TimelineRequest> requests)
    {
        transform.Position = new float3(i % Side, 0, i / Side) * Crowd.Spacing;
        requests.Start(Crowd.Dance, (ushort)((i % Side + i / Side) * 3 % Crowd.Ticks));
    }
}
```

The spawn runs once (the `Crowd` entity is destroyed after it), so the per-frame path is only Tl's systems and the Motion adapter jobs.
Offsets come from the grid position, not a random generator, so the CLI can place the same starts.

**README outline:** 1) what is measured and how; 2) the numbers on the reference machine; 3) why it scales (no entity per clip; one
playback per dancer; kinds reduce per entity); 4) Kind Report output for the crowd; 5) the same run in .NET (`tl bench demos/crowd`).

**First 10 minutes:** press Play, press 3 for 100k, read ms per tick. Run `Tools > Tl > Kind Report`. Run the CLI bench and compare.

**Acceptance (headless):** PlayMode bench test (Burst on, after warm-up, run twice, take the later run as the field notes say): rows for
10k and 100k entities, `ns/entity` per tick and warm allocation. Gates on the same machine and day: warm allocation **0 B**; ns/entity
within **1.2x** of the K bench A row (today 7.3 to 8.5); `Pose` hash of all dancers after 600 steps equal to `tl bench demos/crowd
--entities 10000 --hash` (CLI). Line: `TL-BENCH crowd entities=100000 ns/entity=<x> warmB=0 hash=<H> cli=equal`.

---

## 3. Layout, import and installer

**tl (public core)**
```
packs/Tl.Pack.Motion/       Motion.cs, Tl.Pack.Motion.csproj (netstandard2.1, references Tl.Core only)
packs/Tl.Pack.Stats/
packs/Tl.Pack.Inventory/
tools/Tl.Cli/               the `tl` command (EA): run, bench, hash
demos/inventory/            library.json, loot_run.json, script.json, expected.txt, README.md, host/Program.cs
demos/stats/                duel.json, haste.json, script.json, expected.txt, README.md
demos/arena/                JSON + script + expected.txt (F)
demos/crowd/                dance.json, script.json, expected.txt
demos/Demos.Tests/          dotnet test: runs every demo dir, checks expected.txt
```

**tl.unity (private, `com.tl.unity`)**
```
com.tl.unity/Packs/Motion/      synced pack source + Unity adapter (partial IComponentData, Pose/Tint jobs, bakers); asmdef Tl.Pack.Motion
com.tl.unity/Packs/Stats/       same pattern, plus the Stats starting-values authoring
com.tl.unity/Packs/Inventory/
com.tl.unity/Samples~/ForceDemo/        A (exists)
com.tl.unity/Samples~/PlaybackPanel/    B
com.tl.unity/Samples~/Animation/        C
com.tl.unity/Samples~/Stats/            E
com.tl.unity/Samples~/SameData/         F (JSON copied from tl demos/, checked)
com.tl.unity/Samples~/Events/           G
com.tl.unity/Samples~/Crowd/            H
```
Each sample: `Scripts/` (asmdef `Tl.Demo.<Name>`), `Editor/<Name>Builder.cs` (scene, SubScene, timelines through Unity APIs; runs on
import and from `Tl > Samples > Build <Name>`), `Tests/` (PlayMode, `UNITY_INCLUDE_TESTS`), `README.md`. D has no Unity sample.

- Packs are source-synced from tl by `eng/sync-tl-packs.sh [--check]` (same pattern as `sync-tl-core.sh`: stamp with the pin and a source
  hash, `--check` compares, run by `eng/test-com-tl-unity.sh`). The demo JSON in `SameData` is synced and checked the same way.
- Pack kinds have pack-prefixed names (`MotionPosition`, `StatsDamage`, `InventoryItems`) because a kind is named by its type name and a
  user's own `Damage` must not collide. Pack tracks appear in every project's `+` menu under `Motion/`, `Stats/`, `Inventory/`.
- All samples import into one project without clashes; `eng/check-samples-together.sh` is extended to import all of them and run every
  `TL-DEMO` test.

**Installer (`install/unity.ps1`, `install/unity.sh` in tl), git URL only.** What it must do:
1. Make a Universal 3D project from the editor's URP template (as today), add the package git URL and `com.unity.test-framework`.
2. Import every sample (`Sample.FindByPackage("com.tl.unity", null)`, all entries), then compile: 0 `error CS`.
3. Run each sample's builder (one `executeMethod` that calls every `Tl.Demo.*Builder.Build`), add all demo scenes to the build list, and
   build a `Tl Demos` hub scene (buttons that load each demo by build index).
4. Make sure `com.tl.unity` is not in `testables` (field notes: its own test kinds collide with demos).
5. Run every demo's PlayMode tests in one batch run and print one line per demo with actual and expected numbers; exit non-zero on any
   mismatch.
6. Leave `Tl Demos` open. Print: "open Tl Demos, press Play".
Env/flags: `TL_DEMOS=all` (default) or a comma list; `--no-tests` skips step 5. CLI demos are a separate one-liner from a tl clone (no
release, nothing installed globally): `git clone https://github.com/IAFahim/tl; cd tl; dotnet test demos/Demos.Tests`. The installer runs it
too when a .NET 10 SDK is on the machine and prints the CLI hashes next to the Unity ones (that is F, live).

---

## 4. Order, size, dependencies

**Missing pieces this plan needs**

From the engine-agnostic work (EA):
- EA1 `[TlClip]`, `[TlKind]`, `ITlTrack`, `TlFrame`, `TlCurve`, `TlTimeline` move from `Tl.Unity` into `Tl.Core` (engine-free), so packs
  compile in .NET.
- EA2 Kind packs as plain libraries: a .NET project in tl, source-synced into the Unity package, made into components with one `partial`
  adapter file per type.
- EA3 `tl` CLI: `tl run <dir> [--script] [--hash]`, `tl bench`, baking JSON in process, loading the pack assemblies named by the library.
- EA4 One state hash definition (section F) in both hosts.
- EA5 Unity `Tl > Import Timeline JSON` / `Export Timeline JSON` (JSON <-> `.playable` with Tl tracks), byte-equal round trip.

Small package additions (tl.unity), each under a day:
- `TlBaking.GetTimeline(baker, TimelineAsset)` for MonoBehaviour bakers (adds the timeline to the object's set, like a clip field does)
  and `TlBaking.Ticks(seconds)` (the project tick rate for bakers).
- `TlRequests.StopChannel(byte)` and `StopAll()` on the request buffer.
- Fix issue #24 (menu stub warnings, empty-track error).
- Verify `[Pulse]` and `OnClipStart` + `Events<T>` work on `ITlTrack` through the zero-config generator; if not, file it.

Core gaps (tl), not required for the first version, listed so they are not forgotten: request ops `Seek`, per-playback `Reverse`, per-playback
speed; a held (`Ended`) playback does not move in either direction, so it cannot be rewound (document or change). Unity authoring gaps: Tl
markers in the Timeline window, slot keys, `IBlend` for `ITlTrack`. No tl issue is filed for the request ops yet (the
planner's attempt was not permitted); the first wave-1 agent files it in IAFahim/tl.

**Waves** (each demo is one agent, one worktree, its own sample folder; no two agents edit the same files)

| Wave | Demo / work | Needs | Can run in parallel with | Estimate (agent-days) |
|---|---|---|---|---|
| 1 | Small package additions + #24 fix | nothing | A, B, G, H(Unity) | 1 |
| 1 | A Force polish | #24 fix | B, G, H | 0.5 |
| 1 | B Playback panel (uses Force-style local tracks first, swaps to Motion pack in wave 3) | package additions | A, G, H | 1.5 |
| 1 | G Events (uses a local damage kind first) | package additions, Events check | A, B, H | 1 |
| 1 | H Crowd, Unity half | package additions | A, B, G | 1 |
| 2 | Packs Motion, Stats, Inventory in tl + sync script | EA1, EA2 | each other (3 agents) | 0.5 each |
| 2 | D Inventory (CLI) | EA3, EA4, Inventory pack | E (CLI half), packs | 0.5 |
| 2 | E Stats, CLI half | EA3, EA4, Stats pack | D | 0.5 |
| 3 | C Animation | Motion pack in Unity | E (Unity half), F | 1.5 |
| 3 | E Stats, Unity half | Stats pack in Unity | C | 1 |
| 3 | B and G switch to the packs | packs in Unity | C, E | 0.5 |
| 3 | F Same data | EA5, D, E, C | H CLI half | 1.5 |
| 3 | H Crowd, CLI half + hash equality | EA3, Motion pack | F | 0.5 |
| 4 | Installer (all samples, hub scene, per-demo lines) + samples-together check | all samples | none | 1 |

Total about 14 agent-days; with four agents in waves 1 and 3, about 6 working days after EA1 to EA3 land. Wave 1 can start now and does
not wait for EA. One Unity process at a time per machine (lock directory): wave-1 agents share the Unity lock, so give each its own scratch
project and serialize the batch runs.

---

## 5. Questions for the owner

1. Speed and rewind are whole-world today (time scale and `TimelineStep.Forward`). Is that enough for the panel, or do you want per-timeline
   speed and rewind, which changes Tl Core and the hash rules?
2. The panel's buttons live in a MonoBehaviour that appends one command per click to an ECS buffer and reads one status buffer per frame;
   everything else is Burst. Is that UI boundary acceptable, or should the package own it (a `Tl Panel` component) so users write no
   MonoBehaviour?
3. The packs and the CLI demo data would live in the public tl repo, so anyone can read them. Fine, or keep them in private tl.unity until
   release?
