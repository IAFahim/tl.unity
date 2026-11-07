# Zero-config UX plan (issue #22)

Status: plan, not implemented. Companion: [zero-config-arch.md](zero-config-arch.md) (bootstrap, in-editor bake, generator, builds).
Rules this plan obeys: newest Unity 7 only; hot path is unmanaged `ISystem` + Burst; references are dragged in the Inspector, never typed;
no hand-written Unity YAML; no code comments in shipped code.

The promise, in one sentence: **add one git URL, open Unity's Timeline window, drag clips, press Play, see it move.**

---

## 0. Centerpiece: the Force track (owner's acceptance contract)

The owner's contract (issue #22 comment of 2026-11-07): a coder writes five files, a designer adds a **Force** track in the Timeline window, the
bake adds everything the track needs to the entity, and on Play the object is pushed. The plan fails if the coder needs any file, attribute, asset
or step beyond the code below. Everything in this document serves this path; the Walk/Hit demo of section 4 is built the same way.

### 0.1 Designer journey, step by step (no code, nothing typed)

1. A coder has dropped the five files below into `Assets/` (any folder, no asmdef). Unity compiles once.
2. Dana selects a GameObject with a mesh inside a SubScene (or uses `GameObject > Tl > Timeline Object`, which makes one).
3. `Assets > Create > Tl > Timeline`, name it `Push`. Double-click: Unity's Timeline window opens.
4. Timeline window: `+` lists **Force** next to Unity's own tracks (Activation, Animation, ...). Click it. A track named **Force** appears.
5. Right-click the track > **Add Tl Clip** (it is created as a Force clip, labelled `Force`). A 1 s clip labelled `Force` appears. She drags its edges to 2 s.
6. With the clip selected the Inspector shows Unity's clip header and below it:

```
Force Clip
  Direction    X [ 1 ]  Y [ 0 ]  Z [ 0 ]
  Strength     [ 4   ]
  Falloff      [ curve preview ~~~\__ ]   (click: Unity's curve editor, time 0..1 across the clip)
```

7. She drags `Push.playable` from the Project window onto the GameObject in the Hierarchy. Unity adds a Playable Director with Push assigned and
   Play On Awake on. The Tl foldout under it reads: `Bakes: Force, Force Log (from the Force track).`
8. Press Play. In the Game view (the scene camera framing the object) the object slides along +X for 2 s, fast at first, slowing as the curve
   falls, then stops.
9. Still in Play mode she raises Strength to 10: the next lap pushes further (the timeline's Tl section says When it ends: Loop). `Window > Tl > Monitor` shows
   `Cube  Push  playing 1.2 / 2.0 s   Force +3.1 X`.

What she never did: add a component, pick a "kind", fill a library, type a name, open a terminal.

### 0.2 Coder-side code, corrected final form (target API)

Five files, Assembly-CSharp or any asmdef that references nothing extra (the Tl assemblies are auto-referenced). Not compiled yet: this is the
contract the implementation must meet, proven by acceptance test A (section 7). Names already reconciled with the owner's sketch; changes
listed in 0.3.

`ForceClip.cs`
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

`ForceData.cs`
```csharp
using Unity.Entities;
using Unity.Mathematics;

public struct Force : IComponentData
{
    public float3 Value;
}

public struct ForceLog : IBufferElementData
{
    public float3 Value;
    public int Tick;
}

public struct ForceFalloff : IComponentData
{
    public BlobAssetReference<TlCurveBlob> Curve;
}
```

`ForceKind.cs`
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

`ForceBaking.cs`
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

`ForceSystem.cs`
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

### 0.3 What changed from the owner's sketch, and what the package must therefore do

| # | Sketch | Final | Why |
|---|---|---|---|
| 1 | `[UpdateAfter(typeof(TlSimulationGroup))]` | `[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]` | Tl's group runs inside `FixedStepSimulationSystemGroup` (`Runtime/TimelineSystems.cs`). A system without `UpdateInGroup` lands in `SimulationSystemGroup`, where `UpdateAfter` a group in another parent is ignored with a warning, and `Force` would be applied once per frame while the kind adds once per fixed tick. Inside Tl's group, last: it runs every tick after the kind applied, and `SystemAPI.Time.DeltaTime` is the fixed step. One attribute either way. Package must: rename/alias `TimelineSimulationGroup` to `TlSimulationGroup`, and never mark its own systems `OrderLast`. |
| 2 | `ForceSystem.cs` without `using Tl;` | adds `using Tl;` | `TlSimulationGroup` lives in `Tl`. |
| 3 | `TlCurve` in a clip, `Falloff.Evaluate` in Burst | unchanged in code | Package must make `TlCurve` an unmanaged, inline, fixed-size curve (sampled from the designer's `AnimationCurve` at bake, time 0..1) with a Burst-safe `Evaluate`, drawn in the Inspector as Unity's curve field. |
| 4 | `ForceBaking` per clip | unchanged in code | Package must define: `Bake` runs once per clip on the entity that plays it; `AddComponent`/`AddBuffer` are idempotent; `AddBlob` deduplicates equal blobs. Two clips that give the same entity *different* `ForceFalloff` blobs cannot both fit one component: the bake keeps the first and the second clip's Inspector says `Another Force clip on Cube already sets Force Falloff with a different curve. Only the first is kept on the object; the clip's own Falloff still drives the push.` (Runtime push uses the inline `TlCurve`, so the demo is correct either way.) |
| 5 | `Force` added by `ForceBaking` | also added automatically | `ForceKind.Apply` writes `ref Force`, so the generator already knows the entity needs `Force`; the `AddComponent<Force>()` line is legal and harmless. `ForceBaking` is optional for components a kind writes and required only for extras (`ForceLog`, blobs). |
| 6 | Unity-side `TrackAsset`/`PlayableAsset` classes | none written by the coder | Package ships one `TlTrack` and one `TlClip`; each holds the clip as `[SerializeReference]` to a generated plain `[Serializable]` mirror (native Inspector, Undo, curve editor). `Force` appears in the Timeline `+` menu through a generated menu stub that the package's track editor swaps for a real `TlTrack` (architecture F2, F6). A generated `TrackAsset` is not possible: it loses its data after an Editor restart (F1). |
| 7 | `ForceLog.Tick` | unchanged | Nothing writes `ForceLog` in this contract; it proves buffer baking. The Monitor shows `Force Log (0 entries)`. |
| 8 | "PlayableDirector or a Tl Player" | PlayableDirector only | One way to assign; no second authoring component on the designer path. |

Forbidden extras (each one fails the contract): an asmdef edit, a `[RequiresKinds]`, a library/bridge asset, a JSON file, a menu command, a
generated file the coder must commit, a `Timelines.g.cs` constant, an external tool, a restart.

### 0.4 The whole baking path, from the Timeline window to the entity

Canonical code: [zero-config-arch.md](zero-config-arch.md) section 3. The architecture planner checked these by spike on Unity 7:
- a generated `TrackAsset` loses its data after a restart, but the `[SerializeReference]` mirror survives;
- the generator runs on `Assembly-CSharp`;
- `Baker<PlayableDirector>` reads the timeline, and the coder's `ITlBaker` adds a component, a buffer and a blob;
- a clip edit rebakes only that SubScene;
- `Force` shows in the `+` menu.

This section tells the same path from the user's side. The names match the architecture plan.

```
Timeline window (.playable)                  SubScene bake (Unity's own, incremental)                Entity
 TlTrack "Force" (Template = ForceClip mirror)  TlDirectorBaker : Baker<PlayableDirector>            TlTimelineSet [slot 0: blob]
  TlClip (Data = ForceClip mirror: Direction,    DependsOn timeline, Tl tracks, clip assets,          Playback [], TimelineRequest [Start slot 0]
   Strength, Falloff as AnimationCurve)           settings marker                                     TimelinePlaying (off), TlUnregistered (on)
 Tl settings marker (End, Next, Channel)        per timeline: TlTimelinePacker, in process            ForceKind entry buffer + enable bit
PlayableDirector (Playable, Play On Awake,       per clip: mirror.Write -> ForceClip bytes            Force (Apply target), ForceLog, ForceFalloff
 Initial Time) [+ Tl Also Plays]                 -> ForceBaking.Bake (once per clip)
                                                 -> TLB1 bytes of the timeline -> one blob
                                                reached kinds -> kind buffers + Apply targets
Play: TlLibrarySystem makes the library; TlTimelineRegisterSystem adds new blobs, gives ids, starts Playback. No Library field anywhere.
```

Step by step, what each piece is and who writes it:

| Piece | Written by | What it does |
|---|---|---|
| `PlayableDirector` | Unity (the designer drags the timeline on) | The authoring component. Playable = starts with, Play On Awake = start on spawn, Initial Time = start offset. |
| `Tl Also Plays` | package, added by the `+` in the director's Tl foldout | Other timelines this object may play (object fields). Never in Add Component. |
| `TlTrack`, `TlClip` | package, one real file each | The only Unity Timeline classes. Each holds the clip as `[SerializeReference]` to the generated mirror. |
| `ForceClip_TlData` (mirror), `ForceClip_TlMenu` (menu stub) | generator, on compile, in memory | Mirror = Inspector fields (`float3`, `float`, `AnimationCurve`) and `Write`, which turns them into `ForceClip` and calls `ForceBaking.Bake`. Stub = the `Force` entry in the `+` menu; the package's track editor swaps it for a real `TlTrack` (the stub is never saved). |
| Tl settings marker | package, made by the timeline Inspector's Tl section | When it ends, Play next, Channel. Lives inside the `.playable`, so duplicate and rename keep it. |
| `TlDirectorBaker : Baker<PlayableDirector>` | package (Editor) | Collects the timelines (Playable, Also Plays, Play next closure), calls `DependsOn` on each timeline, track, clip and marker, and packs each timeline into one blob (same TLB1 format as `tlb`, in process, no `dotnet`). It runs `ITlBaker` per clip and adds `Playback`, `TimelineRequest` (Play On Awake), the kind buffers the timelines reach, and every `ref` type of those kinds' `Apply` (here `Force`). |
| `ForceBaking : ITlBaker<ForceClip>` | the coder (contract) | Runs once per clip on the object that plays it. `AddComponent`/`AddBuffer` add each type only once; `AddBlob` keeps the first value. |
| `TlLibrarySystem`, `TlTimelineRegisterSystem` | package (runtime, `ISystem`) | Build the library from the blobs that are loaded, give each timeline an id, resolve Play On Awake, and swap a changed timeline under the same id (live edit). |

**What lands on the Force demo entity** (Unity's Entity Baking Preview, top to bottom):

```
LocalTransform, LocalToWorld, rendering components     Unity
TlTimelineSet            [1] Push                      Tl  timelines this object can play
Playback                 [0]                           Tl  what is playing (filled at runtime)
TimelineRequest          [1] Start slot 0              Tl  from Play On Awake
TimelinePlaying          (disabled)                    Tl
TlUnregistered           (enabled)                     Tl  cleared on the first frame of Play
ForceKind entry buffer + enable bit                    Tl  per-tick inputs of ForceKind
Force                    (0,0,0)                       coder, added because ForceKind.Apply writes it
ForceLog                 [0]                           coder, from ForceBaking
ForceFalloff             blob, 16 samples              coder, from ForceBaking
```

The preview is Unity's engineer view and cannot be relabelled. The designer reads the same facts in plain words in the director's Tl foldout:
`Can play: Push. Starts: Push (Play On Awake). Gets: Force, Force Log, Force Falloff. Engine data: 6 components (hidden).`

**Incremental rebake.**
- Editing a clip changes that clip's sub-asset. `DependsOn` makes Unity rebake only the objects whose timelines include it; an unrelated asset rebakes nothing.
- With the SubScene open (Unity's live baking), the new blob reaches the running world and the register system swaps it while `Playback` keeps its tick. This is the live edit of 2.7 (target under 1 s, spike S4).
- With the SubScene closed, Unity reimports it on the next Play.
- Play On Awake, Initial Time, Also Plays and the settings marker are dependencies too.
- Adding a `[TlClip]` type is a script change: Unity compiles, then rebakes. Editing a timeline never compiles code (no `Kinds.g.cs`).

**What the designer sees while it bakes.**

| Moment | What shows |
|---|---|
| Baking running | Unity's own SubScene import progress (status bar spinner, "Importing SubScene" bar on a full reimport). No Tl window, no modal. |
| Bake done, all fine | Tl foldout: `Baked just now: 1 timeline, 1 Tl track, 2 clips.` Console silent. |
| Clip problem (off-grid, second Force Falloff, missing type) | Message on that clip's Inspector and one Console warning whose click selects the clip. The object still bakes; the clip is rounded or skipped. |
| Object problem (outside SubScene, no Playable, no Tl tracks) | Message in the director's Tl foldout with a fix button (section 6). |
| Coder's `ITlBaker` throws | Console: `Force Baking failed on the clip at 0.50 s in Push (Cube): <exception message>. The clip is skipped.` Tl foldout says the same. |
| Clip struct changed layout, rebake pending | Tl foldout: `Your Force code changed. Tl is waiting for Unity to finish compiling and rebaking.` |

---

## 1. Personas and journeys

| Persona | Who | Never wants to see |
|---|---|---|
| **Dana, designer** | Knows Unity scenes, Inspector, Timeline window, Animation. Does not read C#. | Paths, assembly names, namespaces, JSON, command lines, "kinds", "capacity", "tlb", typed names. |
| **Gabe, gameplay programmer** | Writes C#, knows ECS basics (`ISystem`, `IComponentData`, Bakers). Not a Tl expert. | asmdef surgery, regenerated files to commit, a second tool to install, a library asset to keep in sync. |
| **Owner / reviewer** | Wants to verify in 5 minutes on a fresh project. | Anything that needs an agent to drive it. |

### Dana

- **60 seconds.** Package Manager > Install from git URL > paste. The package's page shows the sample **Hello Tl** with Import. Click Import:
  the scene `Hello Tl` opens by itself. Press Play. A capsule walks across a ground plane in front of a camera, dashes, hits a cube. On-screen text
  says what is playing and the cube's health. Nothing was typed.
- **10 minutes.** Select the capsule. Inspector shows **Playable Director** (Unity's own) with timeline `Walker Moves`. Double-click it: the Timeline
  window opens with tracks named `Tl/Walk` and `Tl/Hit`. She drags the end of the Walk clip, changes **Speed** from 2 to 5 in the Inspector, presses
  Play: the capsule runs faster. While still in Play mode she changes Speed back to 2: within a second the capsule slows. She right-clicks the track
  area, `Tl > Walk`, adds a second clip. She creates a new timeline (`Create > Tl > Timeline`), drags it into the director's **Also Plays** list
  and sets the first timeline's **When it ends** to **Play next** with the new one dragged in.
- **First day.** She builds the three moves of a character (idle, attack, hurt) as three timelines, assigns them by drag-and-drop, uses the
  **Tl Monitor** window during Play to see which timeline runs on which object and at what time, and fixes the one warning she got ("Walk clip
  overlaps another Walk clip; only the higher Priority wins") by reading it in plain words on the clip itself.

### Gabe

- **60 seconds.** Same import. Opens `Samples/Hello Tl/Scripts`. Sees `Force/` with the five files of section 0.2, plus the Walk/Hit files
  built the same way. No `.json`, no generated file checked in.
- **10 minutes.** Adds a new clip type: one file, about ten lines (section 2.9). Returns to Unity: after the normal compile, `Tl > Spin` is in the
  Timeline window's add-track menu with a typed Inspector. Adds an authoring field `public TimelineAsset Dash;`, drags the asset in, and starts it from
  a Burst system with `TlRequests.Start(ref requests, in dasher.Dash)` (section 2.11).
- **First day.** Writes a gameplay system that reacts to a clip starting (pulse), adds a unit test that bakes a timeline in EditMode and steps the
  world, makes a player build. No step outside Unity.

---

## 2. The exact experience

### 2.1 Add the package

1. `Window > Package Management > Package Manager > + > Install package from git URL…`
2. Paste `https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main`. Install.
3. Package Manager pulls `com.unity.entities`, `com.unity.entities.graphics`, `com.unity.timeline` (declared dependencies).
4. Compile finishes. Console shows exactly one info line: `Tl is ready. Import the "Hello Tl" sample from Package Manager to see it run.`
   No dialog, no window popping open, no files written into `Assets/`.

Nothing else. No .NET SDK, no clone, no `tlb`, no install script, no asmdef marker, no project setting to change.

### 2.2 Create the first scene (own scene, not the sample)

1. `File > New Scene` (Unity default: camera + light).
2. `GameObject > Tl > Timeline Object`. The package creates, in one Undo step:
   - a SubScene named `Tl Objects` if the scene has none (reuses the existing one if it has one),
   - inside it a capsule named `Timeline Object` with a **Playable Director** and a new timeline asset `Timeline Object.playable` saved next to the
     scene, opened in the Timeline window.
3. Alternative Unity-native path that must also work: create a GameObject in a SubScene, drag a `.playable` from the Project window onto it
   (Unity adds the Playable Director itself).

### 2.3 Create a Timeline with Tl tracks

1. `Assets > Create > Tl > Timeline` (or Unity's `Create > Timeline`; both work, the Tl one presets frame rate to the tick rate and snapping on).
2. In the Timeline window, `+` lists a `Tl` group: `Tl/Walk`, `Tl/Hit`, plus every track type in the project's code, grouped by the menu path the coder
   chose. Track colors come from the code.
3. Tl tracks have **no binding slot**. They act on the object that plays the timeline. (Unity tracks such as Animation or Activation keep working
   next to them; they simply are not part of the Tl simulation.)
4. Frame rate: the Timeline window shows frames that equal simulation ticks. If the designer changes the frame rate, the Tl section of the
   timeline Inspector says: `This timeline runs at 30 frames per second but the game simulates 60 ticks per second. Clips will be rounded to the
   nearest tick.` with a button **Use 60**.

### 2.4 Drag clips

- Right-click a Tl track > `Add Walk Clip`, or drag on the track. Move, trim, duplicate, copy across timelines: all Unity-native.
- Clip label on the timeline: the clip's short summary, e.g. `Walk · speed 2`, never a type name with a namespace.
- Select the clip: Inspector shows Unity's clip header (Start, End, Duration, ease) and below it the clip's fields as typed controls (section 3).
- Markers: right-click the marker row > `Tl > Cue`. Inspector shows the marker's fields.
- Overlap of two clips on one track: Unity draws the blend region; the Inspector of either clip explains what happens in words from the track type
  (for Walk: `Overlapping Walk clips: the higher Priority wins while both play.`).

### 2.5 Assign it to a GameObject

- The **Playable Director** is the assignment. Its Unity fields mean:
  - **Playable**: the timeline this object starts with.
  - **Play On Awake**: on = starts when the object spawns; off = waits for code or the Inspector.
  - **Wrap Mode**: ignored by Tl. Loop/Hold/Stop belong to the timeline (its Tl section), so one timeline means the same on every object; the
    foldout shows `This timeline loops (set on the timeline). Wrap Mode is not used by Tl.` when they disagree.
  - **Initial Time**: start offset.
  - Update Method other than Game Time: a yellow line under the director: `Tl timelines always follow game time. Update Method is ignored.`
- Under the director, the package draws a **Tl** foldout (not a separate component the designer must add) with **Also Plays**: a list of timeline
  object fields for timelines code or other timelines may start on this object. Dragging is the only way to fill it.
- Nothing to sync. There is no "Sync Directors" menu. Baking reads the director directly.

### 2.6 Press Play

- Press Play. The SubScene bakes as usual (Unity's own progress), the timeline data is part of that bake, the runtime finds it on the entity. The
  object moves on the first frame.
- If baking is slow on first import, Unity's own SubScene progress shows; no Tl-specific wait.

### 2.7 Edit a clip and see it change live

- In Edit mode (later phase, architecture 4.9 item 12): scrubbing the Timeline window playhead moves the object in the Scene view (preview
  through the Editor world, like BovineLabs' `EditorTimelineSystem` and Vex's scrub driver). Leaving the Timeline window restores the object.
  Until then the honest gap is listed in section 8.
- In Play mode: change a clip value, move or trim a clip, add a clip. Within one second the running object follows. The timeline that is playing
  continues from its current time. Console: nothing (silence on success). The Tl Monitor shows `Reloaded Walker Moves`.
- Exiting Play keeps the edits (they were edits to the `.playable` asset, which is normal Unity behaviour for assets).

### 2.8 Unity Timeline window features that must behave

| Feature | Behaviour |
|---|---|
| Undo/Redo | Native, including clip field edits. |
| Duplicate `.playable` | Independent copy, plays on its own; Tl settings copied with it. |
| Rename `.playable` | Nothing breaks; nothing references it by name. |
| Delete a `.playable` still used | Director shows Unity's "Missing"; Tl foldout says `This object has no timeline. Drag one into Playable.` |
| Nested timelines (Control Track) | Not supported in phase 1: Tl section shows `Control Track clips are ignored by Tl.` |
| Group tracks, mute, lock | Mute = not baked. Lock = editor only. |

### 2.9 A coder adds a new clip type (a few lines)

Same shapes as the Force contract (section 0.2), nothing else. A clip that feeds a kind that already exists is two declarations:

```csharp
using Tl;
using UnityEngine;

[TlClip("Movement/Spin")]
public struct SpinClip
{
    [Tooltip("Degrees per second.")] public float Speed;
}

public struct SpinTrack : ITlTrack<SpinClip>
{
    [Feeds(typeof(TurnKind))]
    public static float Evaluate(in TlFrame<SpinClip> frame) => frame.Clip.Speed;
}
```

What Gabe sees: save, Unity compiles once, `Tl > Movement > Spin` appears in the Timeline window menu, its clip Inspector shows **Speed** with
the tooltip. No new asset, no library field, no rebake button. Components the kind writes are added by the bake (section 0.3 row 5); extras go in
an `ITlBaker<SpinClip>`. The `[TlClip]` string is the menu path and the default track name; it is the only text a coder writes, and designers never
type it.

### 2.10 Add a gameplay system

Gabe writes a normal Burst `ISystem` that reads components Tl writes (`Motion`, `Health`) or reacts to pulses. Template in the sample:
`MoveFromMotionSystem` (writes `LocalTransform` from `Motion`), 15 lines, `[UpdateInGroup(typeof(TimelinePresentationGroup))]`. No registration,
no `TimelineSystemMember` attribute for the common case.

### 2.11 Start, stop, pause timelines

**From the Inspector (designer, Play mode).** The Tl foldout under the director becomes live:

```
Tl  (playing)
  Now playing     Walker Moves   1.20 s / 4.00 s   [Pause] [Stop]
  Start           [ Dash              v ]  [Play]
                  (dropdown lists Playable + Also Plays, by asset name)
```

**From another object, no code.** Component `Tl Trigger` (sample + package): fields `When` (dropdown: On Key, On Start, When another timeline ends),
`Key` (key picker), `Target` (object field, drag a GameObject with a director), `Action` (dropdown: Play, Stop, Pause, Resume), `Timeline` (object field,
drag a `.playable`; the field only accepts timelines in the target's Playable or Also Plays, and says so if not).

**From code.** Gabe adds an object field to his own authoring and drags the asset in. The baker turns it into an id. The system never sees a name:

```csharp
using Tl;
using Unity.Entities;
using UnityEngine;

public sealed class DasherAuthoring : MonoBehaviour
{
    public TlTimeline Dash;
}

public sealed class DasherBaker : Baker<DasherAuthoring>
{
    public override void Bake(DasherAuthoring authoring) =>
        AddComponent(GetEntity(TransformUsageFlags.None), new Dasher { Dash = TlBaking.GetTimeline(this, authoring.Dash) });
}

public struct Dasher : IComponentData { public TlTimelineRef Dash; }
```

```csharp
TlRequests.Start(ref requests, in dasher.Dash);
TlRequests.Stop(ref requests, in dasher.Dash);
TlRequests.Pause(ref requests, in dasher.Dash);
TlRequests.Resume(ref requests, in dasher.Dash);
```

`TlTimeline` draws as a timeline object field (drag and drop). `TlBaking.GetTimeline` bakes it to `TlTimelineRef` (the `.playable`'s identity,
unmanaged) and adds that timeline to the object's playable set, so no Also Plays entry is needed for it. Requests resolve to ids at runtime;
Pause/Resume are new request ops (architecture 4.5). No generated id constants anywhere.

### 2.12 Debugging view: Tl Monitor

`Window > Tl > Monitor` (UI Toolkit). Play mode only; in Edit mode it says `Press Play to see what is running.`

```
Tl Monitor                                       World: Default World   [Pause game] [Step]
Search [walker            ]

Walker            Walker Moves    playing   1.20 / 4.00 s   loop     channel Body
                  Dash            queued    starts next
                  What it does this tick:
                    Walk          speed 2  -> Motion X +2.0
                    Hit           ended
Dummy             (nothing playing)
                  Health 80  (took 20 at 3.10 s from Walker Moves / Hit)

Click a row: selects the object in Hierarchy and the timeline in the Timeline window at the current time.
```

- Words, not buffers: "playing / paused / queued / ended", "took 20 damage", values of the components a track writes.
- `Show engine details` toggle (off by default) reveals the raw `Playback` buffer, kind entry counts and capacities for Gabe.
- Entity Baking Preview stays Unity's; the Tl foldout on the director shows a one-line summary instead: `Bakes: Playback (2 timelines), Motion,
  Walk Stat. Engine components hidden.`

---

## 3. What the Inspector shows (mocks)

### Playable Director (Unity) + Tl foldout, Edit mode

```
Playable Director
  Playable           [Walker Moves (Timeline)  o]
  Update Method      Game Time
  Play On Awake      [x]
  Wrap Mode          Loop     (not used by Tl)
  Initial Time       0
  Bindings           (none needed for Tl tracks)
Tl
  Also Plays         [Dash (Timeline)  o]  [Hurt (Timeline)  o]  [+]
  Starting values    Walk Stat  [ 1   ]     Health [ 100 ]
                     (added because the tracks Walk and Hit need them)
  Bakes: Playback, Motion, Walk Stat, Health
```

"Starting values" (later phase, a `TlStartValues` component edited in this foldout; architecture 4.9 item 6) lists only components required by the tracks on any of this object's timelines, with their fields, nicified names and
tooltips. A designer can override; not overriding is fine. This replaces `WalkerAuthoring` in the designer path.

### Timeline asset Inspector (Tl section under Unity's header)

```
Walker Moves (Timeline Asset)
  [Unity: Duration Mode, Frame Rate ...]
Tl
  Channel          [ Body          v ]   (only one timeline plays per channel; starting another replaces it)
                   [ Add channel... ]
  When it ends     ( ) Loop  ( ) Hold last frame  (x) Stop  ( ) Play next [Run (Timeline)  o]
  Status           OK. 2 Tl tracks, 3 clips, 1 cue.
```

Stored in a small Tl settings marker at time 0 inside the `.playable` (made on the first change, shown as a Tl badge), so duplicate and rename
keep it. No marker = Stop, no channel. The director's Wrap Mode is not used (see 2.5).

### Tl track, selected

```
Walk (Tl Track)
  Scale        [ 1.0 ]
  (track color and menu name from code; no Namespace, no Type text)
```

### Tl clip, selected

```
Walk Clip
  [Unity clip header: Start 0.00  End 0.67  Duration 0.67  Ease In/Out]
  Speed        [ 2   ]   units per tick
  Priority     [ 0   ]   higher wins when Walk clips overlap
```

Fields come from the clip struct: `float/int/byte` numeric fields with `[Range]`/`[Min]` honoured, `bool` toggles, enums as dropdowns,
`float2/3` vector fields, `[Tooltip]` shown. Unsupported field types show `Speed: this field type cannot be edited here yet.` in grey, never a
text box.

### Tl Trigger (no-code control)

```
Tl Trigger
  When       [ On Key        v ]
  Key        [ Space         v ]
  Target     [Walker (Playable Director)  o]
  Action     [ Play          v ]
  Timeline   [Dash (Timeline)  o]
```

### Hidden from the designer (must not appear in any Inspector on the designer path)

`Timeline Library Asset` and every field on it; `Timeline Bridge Asset`; `Timeline Director Authoring`; `Timeline Player Authoring` (its baker logic
moves under the director); `Library Authoring` / `TextAsset` library field; `Kind Profile`; any `string[]` of names; `Kinds` list; Entity Baking
Preview rows for `Playback`, `TimelineRequest`, `*Entry`, `*Kind*` (still visible in Unity's own Entities windows for engineers).

---

## 4. The demo that ships: "Hello Tl"

Imported from Package Manager with one click; on import, the sample's editor code opens its scene. Assets ship as real files built in the Editor by
a generator script in the package repo (never hand-written YAML), like BovineLabs' `TimelineFeatureTourSampleBuilder`.

Scene contents:

- Main Camera framing a 20 m ground plane at a 30 degree angle; directional light; URP default materials (render-pipeline-agnostic fallback if URP
  is not installed: Built-in materials, decided by architecture).
- SubScene `Hello Tl Objects` with:
  - **Walker**: orange capsule. Playable Director: `Walker Moves` (Play On Awake, Loop): walks right 2 s, walks left 2 s, a `Hit` clip at 1.5 s. Also
    Plays: `Dash`.
  - **Dummy**: grey cube with a world-space health bar. Takes the Walker's hits.
  - **Crate**: blue cube pushed by the `Push` timeline (one Force track, Strength 4, falling curve, Loop with a 1 s pause clip gap). Its
    scripts are exactly the five files of section 0.2, in `Samples/Hello Tl/Force/`, so the sample *is* the acceptance contract.
- Screen overlay (UI Toolkit), large readable text, top-left:

```
Hello Tl
Walker   playing Walker Moves   1.2 / 4.0 s
Dummy    health 80
Space  dash        P  pause / resume        S  stop / start
Open the Timeline window: select Walker, double-click Walker Moves.
```

- Keys are wired by `Tl Trigger` components (visible, editable, no code).
- What a new user learns by poking: select Walker, edit Speed in Play mode, see it change. That is the tutorial.

Rejected: auto-creating a scene on package import with no click (writes into `Assets/` without consent, surprises existing projects). Import is
one click and the scene opens itself.

---

## 5. Delete list (designer path)

Removed from the designer path entirely (code may stay as internal engine detail or for tests, but no doc, menu, Inspector or sample shows it):

| Today | Fate |
|---|---|
| `Timeline Library Asset` (Folder, Timeline Ids, Game Assembly, Namespace, Kinds File, Tlb, Kind Sets) | Deleted from the user path. Library is built in the Editor from the timelines a SubScene uses. |
| `Tlb` path, `dotnet`, .NET SDK, cloning `tl`, `TL_TLB` | Deleted. No external tool. |
| `Kinds File` / `Kinds.g.cs` checked into user projects, capacity bake menu | Deleted from the user path; capacities computed at bake. |
| `Game Assembly`, `Namespace`, ids class path | Deleted. Inferred from types. |
| `Kind Sets`, `Kind Profile`, `Kinds` string list, `[RequiresKinds]` in the common case | Deleted. Reachable kinds come from timelines on the object plus `TlTimeline` fields. |
| `Timeline Player Authoring` with `Start With` / `May Start` typed strings | Deleted. Playable Director + Also Plays object fields. |
| `Timeline Bridge Asset` (Output, Ids File, Bake On Save, Timelines rows with Name/End/Next/Channel) | Deleted. Per-timeline settings live on the timeline (section 3). |
| `Timeline Director Authoring`, `Sync()`, `Tools > Tl > Timeline Bridge > Sync Directors`, `Bake All Bridges` | Deleted. |
| `LibraryAuthoring` + `game.bytes` TextAsset | Deleted. Runtime finds data via bootstrap. |
| JSON authoring (`library.json`, `run.json`...) | Deleted from Unity docs and samples. JSON stays as an engine/CLI format for the plain C# path. |
| `library.json` `channels` list | Replaced by the Channel dropdown with "Add channel...". |
| `tickRate` in JSON | Project setting `Project Settings > Tl > Ticks per second` (default 60), with a frame-rate check on timelines. |
| `WalkerAuthoring` just to add stats | Replaced by "Starting values" in the Tl foldout. |
| Install scripts (`unity.sh`, `unity.ps1`, `--demo`) as the way in | Not on the Unity user path. Keep only for CI. |
| `EcsDemo` sample (JSON + bytes, no camera) | Replaced by **Hello Tl**. Kept internally as a test fixture if needed, not listed in Package Manager. |
| USING-ECS.md "Step 1 to 10" | Rewritten: section 2 of this plan becomes the guide; engine internals move to an appendix for engineers. |
| `Tools > Tl > ...` menus for baking | Deleted. Only `Window > Tl > Monitor` and `GameObject > Tl > Timeline Object`, `Assets > Create > Tl > Timeline`. |

---

## 6. Error and empty-state messages

Plain words, where the user is looking (on the object, clip or window), with a fix button when one exists. Console gets the same text once,
clickable to select the object.

| Situation | Where | Message | Fix button |
|---|---|---|---|
| Director with Tl tracks outside a SubScene | Tl foldout | `This object is not in a SubScene, so Tl will not run it.` | **Move to SubScene** |
| Director has no Playable | Tl foldout | `This object has no timeline. Drag one into Playable.` | **Create timeline** |
| Timeline has no Tl tracks | Timeline Tl section | `No Tl tracks yet. In the Timeline window click + and pick a Tl track.` | none |
| Track with no clips | Track Inspector | `This track is empty. Right-click it and add a clip.` | none |
| Frame rate mismatch | Timeline Tl section | see 2.3 | **Use 60** |
| Clip starts between ticks | Clip Inspector | `Starts at 0.508 s, between ticks. It will start at 0.500 s.` | **Snap** |
| Play next points at itself in a 1-tick loop | Timeline Tl section | `Play next is this same timeline and it is empty. It would restart forever.` | none |
| Code starts a timeline the object cannot play | Console, once per object | `Walker was asked to play Dash, but Dash is not in its Playable or Also Plays. Drag Dash into Also Plays on Walker.` | click selects Walker |
| Tl type removed from code but used in a timeline | Track | `This track's code was removed (Game.Spin). It is ignored. Delete the track or restore the code.` | **Delete track** |
| Script compile errors | Tl foldout | `Your scripts have errors, so Tl shows the last good version. Fix the errors in the Console.` | none |
| Bake failure (internal) | Console + Tl foldout | `Tl could not prepare Walker Moves: <one sentence>. Your last working version is still used.` | **Show details** |
| No entities playing in Play mode | Tl Monitor | `Nothing is playing. Objects with a Playable Director inside a SubScene appear here.` | none |
| Edit mode | Tl Monitor | `Press Play to see what is running.` | none |
| Entities Graphics missing (nothing visible) | Console on Play, once | `Objects in SubScenes need Entities Graphics to be visible. It should have been installed with Tl; reinstall the package.` | none |

Never shown to a designer: stack traces, file paths, `tlb`, assembly names, "kind", "capacity", "slot", "Playback buffer".

---

## 7. Acceptance tests (5 minutes, fresh project)

### Commands (PowerShell; newest Unity 7 editor)

```powershell
unity install 7 --yes --accept-eula
$Unity = (Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory | Sort-Object Name | Select-Object -Last 1).FullName + '\Editor\Unity.exe'
$P = "$env:TEMP\TlAccept"; Remove-Item $P -Recurse -Force -ErrorAction SilentlyContinue
& $Unity -batchmode -quit -createProject $P -logFile "$P-create.log"
$m = Get-Content "$P\Packages\manifest.json" -Raw | ConvertFrom-Json
$m.dependencies | Add-Member -NotePropertyName 'com.tl.unity' -NotePropertyValue 'https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main' -Force
$m | ConvertTo-Json -Depth 10 | Set-Content "$P\Packages\manifest.json" -Encoding utf8
& $Unity -projectPath $P
```

Automated check (once the package ships its acceptance entry point, owned by architecture):

```powershell
& $Unity -batchmode -projectPath $P -executeMethod Tl.Unity.Editor.Acceptance.Run -logFile "$P-accept.log" -quit
Select-String -Path "$P-accept.log" -Pattern 'TL-ACCEPT'
```

Expected: `TL-ACCEPT PASS import=ok compileErrors=0 sample=ok entitiesPlaying>=1 walkerMoved=yes liveEdit=yes`.

### Human checklist (tick all, no typing except the URL)

1. [ ] After adding the URL and opening, Console has 0 errors and one `Tl is ready` line. `Assets/` is unchanged.
2. [ ] Package Manager > Tl > Samples > Hello Tl > Import. The scene opens by itself.
3. [ ] Press Play: the Game view shows a ground, a moving orange capsule, a grey cube, readable text. Within 2 s the cube's health drops.
4. [ ] Press Space: the capsule dashes. P pauses it, P resumes.
5. [ ] Select Walker in Hierarchy: Inspector shows Playable Director and a Tl foldout only. No Library, Bridge, Folder, Tlb, Namespace, Kinds,
       or text box asking for a name anywhere.
6. [ ] Double-click `Walker Moves`: Timeline window shows Tl tracks with clips. Select the Walk clip: Speed and Priority as number fields.
7. [ ] In Play mode set Speed to 6: the capsule speeds up within 1 s without leaving Play.
8. [ ] Stop Play. `File > New Scene`, `GameObject > Tl > Timeline Object`, in the Timeline window `+ > Tl > Walk`, add a clip, Speed 3. Press Play:
       the new capsule moves.
9. [ ] `Window > Tl > Monitor` in Play mode lists both objects and what they play, in words.
10. [ ] Test A (Force from scratch, below) passes.
11. [ ] `File > Build And Run` (Windows): the sample scene runs the same in the player.

Fail = any step needs a typed name, a path, a terminal command, a second package, or reading docs.

### Test A: build the Force demo from scratch (owner's contract, about 5 minutes)

Same fresh project as above, package installed, no sample imported. This is the owner-runnable acceptance of architecture phase 4.

1. [ ] Copy the five files of section 0.2 into `Assets/Force/` exactly as written (no asmdef). Console: 0 errors, 0 Tl warnings, no
       "Ignoring invalid [UpdateAfter]" line.
2. [ ] `File > New Scene`. `GameObject > Tl > Timeline Object`. The scene now has a camera, a light, a SubScene and a capsule in it.
3. [ ] `Assets > Create > Tl > Timeline`, name `Push`. Open it. The Timeline `+` menu shows `Force`. Add the track, add a clip, stretch it to 2 s.
4. [ ] Select the clip: Inspector shows **Direction** (three number fields), **Strength** (number), **Falloff** (curve field that opens Unity's curve
       editor). Set Direction 1,0,0, Strength 4, draw a falling curve.
5. [ ] Drag `Push` onto the capsule. Playable Director appears with Push, Play On Awake on. Tl foldout says it bakes Force and Force Log. No other
       component was added by hand.
6. [ ] Press Play: in the Game view the capsule moves to +X, fast then slowing, and stops after 2 s.
7. [ ] Entities Hierarchy (Unity) on the capsule's entity: `Force`, `ForceLog` buffer, `ForceFalloff` with a valid blob are present.
8. [ ] In Play mode set Strength 10: the next loop pushes further, no exit from Play.
9. [ ] Add a second Force track (Direction 0,1,0) overlapping the first: the capsule moves diagonally while both play (kind `Combine` adds).
10. [ ] Delete `ForceBaking.cs`: after compile the capsule still moves (Force is added because `ForceKind` writes it); `ForceLog` and `ForceFalloff`
        are gone. Restore the file.

Automated twin: `Tl.Unity.Editor.Acceptance.RunForce` (architecture owns it) writes the same five files into a temp folder of the scratch project,
builds the scene and timeline through Editor APIs, enters Play, and prints
`TL-ACCEPT-FORCE PASS compileErrors=0 components=Force,ForceLog,ForceFalloff movedX>0.5 diagonal=yes`.

---

## 8. Compared with BovineLabs Timeline's first minute

Honest table. "BL" = `com.bovinelabs.timeline` 2.0.0-pre.7; "Vex" = vex-studio timeline packages built on it.

| Step | BovineLabs / Vex | Tl today | Tl after this plan |
|---|---|---|---|
| Install | BL: package manager package first, then install BL Timeline (or git URL; needs BL Core). Two steps. | One URL + .NET SDK + clone + build `tlb` + install script. | One URL. **Better than BL.** |
| Author | Unity Timeline window, `DOTSTrack`/`DOTSClip`, menu names like `DOTS/Cue Track`, `Vex/Transform/Spin`. Clip fields are plain serialized fields: native Inspector, Undo, tooltips for free. | Timeline window works but needs a Bridge + Library asset; fields stored as text. | Same as BL. BL is **better at field editing today** (native `SerializedProperty`); we must match it, not approximate it. |
| Bindings | BL tracks bind to a Transform/entity; one timeline can drive many objects. | Tracks act on the playing entity only. | Same as today; **BL is more flexible** (multi-object cutscenes). Open question 9. |
| Start playing | BL: `SetComponentEnabled<TimelineActive>(entity, true)` from code; sample uses a builder + UI. Vex: Play On Awake / Wrap Mode / Initial Time read from the director, which is very intuitive. | Typed names in Timeline Player. | Vex-style: director fields mean what they say, plus drag-in Also Plays, `TlTimeline` fields. |
| Edit preview | BL: `EditorTimelineSystem` drives the Editor world from the Timeline window playhead: scrub and see. Vex adds a scrub driver, a debugger window and a bake "Doctor". | None. | Required (2.7, 2.12). Until shipped, **BL/Vex are better.** |
| Sample | BL: one sample, scene + UI Toolkit panel, menu `BovineLabs > Samples > Timeline Play`; generated assets via a builder script. Rich but large (custom tracks, nesting, cues, crossings), aimed at engineers. | No camera; JSON; bytes. | Smaller and designer-first; BL's generator approach copied. |
| Custom track | BL: two classes (`DOTSClip` + `DOTSTrack`) plus a system with `TrackBlendImpl` and a job: about 60 lines for a float3 track. | Data structs + 3 one-line Unity classes + Bridge asset + rebake. | Force contract: five small files, about 70 lines including a kind, a baker and a Burst job; a clip feeding an existing kind is about 12 lines (2.9). BL's clip `Bake` adds components with Unity's own `Baker`, which is just as direct as our `ITlBaker`. **Even** on lines; better than BL in setup if the generator delivers (no `TrackAsset` class). |
| Runtime model | Entity per clip/track, timers, mixers. Flexible, heavier. | No entity per clip; kinds reduce per entity; Burst. | Unchanged. Our strength; not a UX selling point to designers. |
| Docs | Overview 66 lines; getting started is 3 steps. | USING-ECS 1190 lines, 10 steps. | Getting started under 40 lines, the section 2 path. |

What BL does that we should copy outright: native serialized clip fields; Timeline-window scrubbing into the Editor world; menu-path display names
and `[TrackColor]`; a sample generator script that builds scene and assets in the Editor. What Vex adds that we should copy: director fields read
literally at bake; a "Doctor" that explains mismatches on the director; a debugger window.

---

## 9. Open questions for the architecture planner

All answered in [zero-config-arch.md](zero-config-arch.md) section 4.9; this plan is reconciled with those answers. Still open: spikes S2
to S6 (architecture section 6), in particular S3 (the real Timeline window, Undo of the menu swap) and S4 (live edit latency).

1. Can the Tl data for a timeline be produced inside the Editor (ScriptedImporter or Baker over `TimelineAsset`), with no external process, fast
   enough that saving a clip and pressing Play feels instant?
2. Where does the runtime find the data with no Library field: blob on the director entity in the SubScene, or one hidden generated asset? What
   happens with several SubScenes sharing one timeline (dedupe)?
3. Clip type in about 10 lines: can the package emit the Unity `TrackAsset`/`PlayableAsset` classes from the attributed struct, given Unity's rule
   that a `ScriptableObject` class needs a same-named file? If not, what is the smallest hand-written class (one line per type)?
4. Kind capacities without regenerated C#: can they move to baked data so editing a timeline never triggers a script compile?
5. Per-timeline Channel and When-it-ends storage inside the `.playable` (hidden settings marker or track?), surviving duplicate/rename. Should
   Wrap Mode on the director be the single source for Loop/Hold/Stop, leaving only Channel and Play next for the timeline?
6. Auto-adding components that tracks need (and "Starting values" overrides) from the director baker: feasible from the track types' `Feeds` /
   `Evaluate` signatures?
7. Pause/Resume request ops (`PlaybackFlags.Paused` exists in Core; no `TimelineRequest` op).
8. Live edit in Play mode: hot-swap through `PollReload`, keeping current playback time; what if a clip type's layout changed?
9. Bindings: phase 1 keeps "acts on the playing object". Is targeting another object's entity from a track (a binding slot) in scope later?
10. Entities Graphics and render pipeline: should `com.tl.unity` depend on `com.unity.entities.graphics` so SubScene objects are visible, and does
    the sample need URP?
11. Tick rate as a project setting: where is it stored and baked?
12. Editor-world scrub preview: which world and system group, and how to restore authoring state when the Timeline window loses focus?
13. Force contract: confirm the `[SerializeReference]` mirror-class route (0.3 row 6) works for clips inside a `.playable` sub-asset, survives
    domain reload and renames (`[MovedFrom]`), and that Timeline's track menu can list `Tl > Force` without a per-type `TrackAsset` class.
14. Force contract: `TlCurve` inline size (samples) and accuracy; `TlFrame.Progress` = clip-local 0..1; `ITlBaker` per-clip semantics and
    `AddBlob` dedupe (0.3 row 4); kinds whose `Apply` targets an `IComponentData` the coder owns (`Force`).
15. Force contract: Tl assemblies must be auto-referenced and the generator must run on Assembly-CSharp, so the five files need no asmdef.
