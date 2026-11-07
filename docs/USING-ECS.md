# Using Tl in Unity ECS

A guide from an empty Unity project to a running demo: clips on tracks,
several timelines playing on one entity, rewind, game-code pulses, and a cue
that the presentation layer can read. Everything runs in Burst `ISystem`s on
unmanaged data.

Tested on the newest Unity 7 editor (tested on 7000.0.0a16). Other editors
and player builds are not covered here.

Status: from an empty project, steps 1 to 10 work: install, `tlb`, library,
kinds, systems, bakers, scene and SubScene, capacity bake and library bake
with 0 `error CS`, and the PlayMode test of section 7 passes in the default
world (Walker health 58, Dummy health 93, two cues, rewind exact, 0 dropped).

Other ways in:

- Plain `MonoBehaviour`, no ECS: [USING-PLAIN-CSHARP.md](USING-PLAIN-CSHARP.md).
- Draw timelines in Unity's Timeline window: [TIMELINE-BRIDGE.md](TIMELINE-BRIDGE.md).
- How it compares with the BovineLabs timeline: [COMPARISON.md](COMPARISON.md).
- Reference for kinds, sizing and the editor tools: [KINDS.md](KINDS.md).

## 1. What Tl is

- Tl is a timeline library: a designer places clips on tracks in json; a baker
  packs them into one file, `game.tlib`.
- A timeline never lives on an entity. The library holds each timeline once.
  An entity holds a short list of playbacks: `{timeline, tick}` pairs.
- Each playback has its own tick. Ticks move once per step, forward or
  backward, so rewind is exact.
- You write the clip types, the tracks and the kinds in C#. Tl contains no
  game behaviour.
- A kind is a value type, a `Combine` rule and an `Apply` method. Clips feed
  a kind; the kind writes your data.
- The step runs in Burst `ISystem`s over unmanaged data. Nothing in the step
  path calls managed code.

### Mental model

```
Requests  ->  Tick  ->  Your systems  ->  Drain
```

1. Requests: start and stop requests, and game pulses, queued since the last
   step.
2. Tick: `TimelineKindTickSystem` applies the requests, advances every
   playback by one tick, and writes one entry per active clip into the kind
   buffers of the entity.
3. Your systems: one generated job per `[TimelineSystem]` combines the entries
   of each kind and runs its `Apply` on your columns.
4. Drain: `TimelineEventSystem` clears the closed-playback buffers.

Vocabulary:

| Word | Meaning |
|---|---|
| Column | A plain struct of yours (`Motion`, `Health`). One component per entity. |
| Kind | `[TimelineKind]` struct with `Combine` and `Apply`. Named by its type name. |
| Feeder | `[Feeds]` method on a track consumer. Runs every step while the clip is active. |
| Pulse | `[Pulse]` method. Fires one value at clip start, clip end or a marker. |
| Channel | An exclusive lane per entity. A new timeline on a channel replaces the old one. No channel stacks. |
| Library | `library.json`, the timeline files, and the baked `game.tlib`. |

## 2. Install

### Quick start

One pasted line (needs git, the .NET 10 SDK, `gh auth login`; installs Unity 7 if missing) makes or updates a Unity 7 project with Tl ready for game code. Safe to rerun:
```sh
curl -sSL https://raw.githubusercontent.com/IAFahim/tl/main/install/unity.sh | bash -s -- <project-path>
```
```powershell
$env:TL_PROJECT='C:pathMyGame'; irm https://raw.githubusercontent.com/IAFahim/tl/main/install/unity.ps1 | iex
```

### Package

`Packages/manifest.json`:

```json
"com.tl.unity": "https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"
```

Verified on a fresh Unity 7000.0.0a16 project: it resolves and compiles with 0
errors. The package brings `com.unity.entities` and `com.unity.timeline` with
it.

### Tools outside Unity

You need the .NET 10 SDK and one clone. It is not a Unity project. The repo is
about 220 MB, so the first clone takes minutes (`--depth 1` is enough).

```sh
git clone --depth 1 https://github.com/IAFahim/tl.git
cd tl && dotnet build tools/Tl.Bake -c Release
```

This builds `tlb` as `tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll`. Run it
as `dotnet Tl.Bake.dll <command>`. It needs the `kinds` command, which the
`tl.bake` 2.0.1 package on NuGet does not have, so build from `main`.

The entity glue code (buffers, tags, `TimelineJobs`) comes from the generator
that ships in the package. You run nothing for it (step 1 and step 5).

`TL_TLB` is read only by the package's own EditMode test. In your project you
point the Timeline Library asset's `Tlb` field at `Tl.Bake.dll` (or at a `tlb`
on your `PATH`).

## 3. Build the demo

The demo has a walker that runs, dashes and takes damage, and a dummy that
only takes damage. Names are PascalCase constants from the library: `Run`,
`Dash`, `Stomp`.

Layout under `Assets/Demo`:

```
Demo.asmdef
Kinds.cs
Systems.cs
WalkerAuthoring.cs
DamageableAuthoring.cs
LibraryAuthoring.cs
DemoDirector.cs
Timelines/   library.json, run.json, dash.json, stomp.json
```

### Step 1: the assembly

Authoring components, kinds and systems go in one game assembly. It must
allow unsafe code and reference the Tl assemblies. The reference to
`Tl.Unity.Generate` opts the assembly in to the package's source generator: on
each compile it writes the glue code for your kinds and `[TimelineSystem]`
structs into the assembly. Without that reference nothing is generated and
`TimelineJobs`, `LocomotionEntry` and the other generated names do not exist.

File: `Assets/Demo/Demo.asmdef`

```json
{
  "name": "Demo",
  "rootNamespace": "Demo",
  "references": [
    "Tl.Core",
    "Tl.Unity",
    "Tl.Unity.Authoring",
    "Tl.Unity.Generate",
    "Unity.Burst",
    "Unity.Collections",
    "Unity.Entities",
    "Unity.Entities.Hybrid",
    "Unity.Mathematics"
  ],
  "allowUnsafeCode": true,
  "autoReferenced": true
}
```

### Step 2: columns, kinds, tracks, clips, feeders, systems

All of this is one file. Read it top to bottom.

- Columns: `Motion`, `Health`, `WalkStat`, `CueLog`.
- Kinds: `Locomotion` (the highest priority wins, then writes `Motion.X`),
  `Damage` (sums pulses, subtracts from `Health`), `Cue` (each pulse applies
  once; counts cues).
- Track and clip types: a track holds settings and a blend, a clip holds the
  payload. Every track implements `IBlend<TClip>`.
- Feeders: `Walk.Evaluate` feeds `Locomotion` while a walk clip is active.
  `Hit.Start` and `Hit.Announce` pulse `Damage` and `Cue` when a hit clip
  starts. A pulse method returns the kind's value type.
- Systems: `Movement` and `Vitals` list the columns that kinds may write.
  Each kind belongs to the system whose columns its `Apply` uses.

File: `Assets/Demo/Kinds.cs`

```csharp
using System;
using Tl;

namespace Demo
{
    public struct Motion { public float X; }
    public struct Health { public int Value; }
    public struct WalkStat { public float Value; }
    public struct CueLog { public int Count; public int Last; }

    public struct Speed { public float Value; public byte Priority; }

    [TimelineKind]
    public struct Locomotion
    {
        public static void Combine(ref Speed total, in Speed next) { if (next.Priority >= total.Priority) total = next; }
        public static void Apply(in Speed total, ref Motion motion) => motion.X += total.Value;
    }

    [TimelineKind(Accepts = KindEntries.Pulses)]
    public struct Damage
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int total, ref Health health) => health.Value -= total;
    }

    [TimelineKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct Cue
    {
        public static void Apply(in int id, ref CueLog log)
        {
            if (id == 0)
                return;
            log.Count++;
            log.Last = id;
        }
    }

    public struct WalkClip { public float Speed; public byte Priority; }
    public struct WalkTrack : IBlend<WalkClip>
    {
        public float Scale;
        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result) => result = first;
    }

    public struct HitClip { public int Amount; public int CueId; }
    public struct HitTrack : IBlend<HitClip>
    {
        public float Scale;
        public void Blend(in HitClip first, in HitClip second, float factor, out HitClip result) => result = first;
    }

    public struct Walk : ITrack<WalkTrack, WalkClip>
    {
        [Feeds(typeof(Locomotion))]
        public static Speed Evaluate(in Frame<WalkTrack, WalkClip> f, in WalkStat stat)
            => new Speed { Value = f.Direction * f.Clip.Speed * f.Track.Scale * stat.Value, Priority = f.Clip.Priority };
    }

    [TimelineSystemMember(typeof(Vitals))]
    public struct Hit : ITrack<HitTrack, HitClip>
    {
        [Pulse(typeof(Damage), On = ClipEvent.Start)]
        public static int Start(in Frame<HitTrack, HitClip> f) => f.Direction > 0 ? f.Clip.Amount : 0;

        [Pulse(typeof(Cue), On = ClipEvent.Start)]
        public static int Announce(in Frame<HitTrack, HitClip> f) => f.Direction > 0 ? f.Clip.CueId : 0;
    }

    [TimelineSystem]
    public ref partial struct Movement
    {
        public Span<Motion> Motion;
        public ReadOnlySpan<WalkStat> WalkStat;
    }

    [TimelineSystem]
    public ref partial struct Vitals
    {
        public Span<Health> Health;
        public Span<CueLog> Cues;
    }
}
```

Notes on the file:

- `f.Direction` is +1 forward and -1 backward. A feeder that multiplies by it
  and a pulse that returns 0 backward are what make rewind exact.
- A kind is named by its type name. Names must be unique across all assemblies
  in the project.
- A `[TimelineSystem]` field named `Motion` of type `Span<Motion>` becomes the
  component `Movement_MotionData` (system name, field name, `Data`) with the
  struct in its `Value` field.
- Do not use the namespace segment names `Unity` or `Tl` for your own
  namespaces; inside such a namespace `Unity.Entities` binds to your
  namespace. Generated code uses `global::` for this reason.

### Step 3: author the library

A library is a folder with `library.json` and one json file per timeline.
`data` fields map onto struct fields by name. `loop`, `channel`, `next`,
`hold` and `markers` are described in the `tl` repository, `README.md`
("Data").

File: `Assets/Demo/Timelines/library.json`

```json
{
  "tickRate": 60,
  "channels": [ "locomotion", "action" ],
  "timelines": [ "run.json", "dash.json", "stomp.json" ],
  "kinds": {
    "Damage": { "gamePulses": 8 }
  }
}
```

`kinds.Damage.gamePulses` reserves room for eight game-code pulses per step
on one entity (see Sizing in section 8).

File: `Assets/Demo/Timelines/run.json`

```json
{
  "name": "run",
  "duration": 40,
  "loop": true,
  "channel": "locomotion",
  "tracks": [
    {
      "namespace": "Demo", "type": "WalkTrack", "data": { "Scale": 1 },
      "clips": [
        { "namespace": "Demo", "type": "WalkClip", "start": 0, "end": 40, "data": { "Speed": 2, "Priority": 0 } }
      ]
    }
  ]
}
```

File: `Assets/Demo/Timelines/dash.json`

```json
{
  "name": "dash",
  "duration": 20,
  "tracks": [
    {
      "namespace": "Demo", "type": "WalkTrack", "data": { "Scale": 1 },
      "clips": [
        { "namespace": "Demo", "type": "WalkClip", "start": 0, "end": 20, "data": { "Speed": 5, "Priority": 10 } }
      ]
    }
  ]
}
```

File: `Assets/Demo/Timelines/stomp.json`

```json
{
  "name": "stomp",
  "duration": 8,
  "channel": "action",
  "tracks": [
    {
      "namespace": "Demo", "type": "HitTrack", "data": { "Scale": 1 },
      "clips": [
        { "namespace": "Demo", "type": "HitClip", "start": 2, "end": 4, "data": { "Amount": 25, "CueId": 1 } },
        { "namespace": "Demo", "type": "HitClip", "start": 5, "end": 7, "data": { "Amount": 10, "CueId": 2 } }
      ]
    }
  ]
}
```

`run` is on the `locomotion` channel and loops. `dash` has no channel, so it
stacks on `run`; its priority 10 beats `run`'s priority 0 in `Locomotion.Combine`
while it plays. `stomp` is on `action`, so it plays beside `run`: two timelines
at once.

To draw these in the Timeline window instead of writing json, see
[TIMELINE-BRIDGE.md](TIMELINE-BRIDGE.md).

### Step 4: the Timeline Library asset

`Assets > Create > Tl > Timeline Library`. Name it `DemoLibrary` and fill in:

| Field | Value |
|---|---|
| Folder | `Assets/Demo/Timelines` |
| Timeline Ids | `Demo.Timelines` |
| Game Assembly | `Demo` |
| Namespace | `Demo` |
| Kinds File | `Assets/Demo/Kinds.g.cs` |
| Tlb | the full path of `Tl.Bake.dll` |
| Kind Sets | `Exact` |

Wait for Unity to compile. The generator runs now and writes the glue code
for `Kinds.cs`. The Console shows three `TLUNITY9103` warnings (`KindCapacity.Cue`,
`Damage` and `Locomotion` do not exist yet). That is expected: step 5 writes
them. There must be 0 `error CS`.

### Step 5: bake capacities and bake the library

Two commands. Run the first in Unity, the second in a shell.

1. `Tools > Tl > Bake Kind Capacities`. It copies the compiled script
   assemblies and the engine assemblies into `Temp/TlKindBake` and runs
   `tlb kinds`. It writes `Assets/Demo/Kinds.g.cs` (the `KindCapacity`
   constants) and logs each kind's capacity. This also runs by itself after
   each script reload and when a `.json` in the library folder is imported, so
   the file may already exist. `Temp/TlKindBake` lives while the Editor is
   open. After `Kinds.g.cs` appears the scripts recompile and the
   `TLUNITY9103` warnings go away.

2. With the Editor still open, from the project folder, bake the library. This
   writes `game.tlib` and the ids class `Timelines.g.cs`:

   ```sh
   dotnet <tl>/tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll library Assets/Demo/Timelines      --assembly Temp/TlKindBake/Demo.dll      --out Assets/Demo/Timelines/game.tlib      --ids Assets/Demo/Timelines/Timelines.g.cs      --kinds Assets/Demo/Kinds.g.cs      --namespace Demo
   cp Assets/Demo/Timelines/game.tlib Assets/Demo/Timelines/game.bytes
   ```

   On Windows use `copy` for the last line. Pass `--kinds`: without it `tlb`
   writes a second `Kinds.g.cs` next to `game.tlib` and the project stops
   compiling (`CS0101`, duplicate `KindCapacity`).

   `game.bytes` is the same file under a name Unity imports as a `TextAsset`.
   The runtime loads the library from that asset (step 7). The baker reads
   `game.tlib`, so keep both and copy after every bake.

There is no third command. The glue code (`LocomotionEntry`, `DamageEntry`,
the `*Active` and `*Enabled` tags, `LocomotionKind`, `DamageKind`, the
`Movement_*Data` and `Vitals_*Data` components, `TimelineJobs.Movement` and
`TimelineJobs.Vitals`) is rewritten by the generator on every compile of `Demo`.
Change a kind, a column or a `[TimelineSystem]` and recompile: the glue follows.

The generator also writes a registration class for these components. Unity
registers an assembly's component types from its source files, which does not
include generator output, so the package's IL post-processor
(`Unity.Tl.CodeGen`) calls that class from the assembly's load hook. You do
nothing for it; it runs in the Editor, in the SubScene import worker, in
Play mode and in player builds.

### Step 6: the systems

Your two systems schedule the generated jobs. They read the library and the
step direction from unmanaged singletons that the package creates per world.

File: `Assets/Demo/Systems.cs`

```csharp
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

namespace Demo
{
    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct MovementSystem : ISystem
    {
        TimelineJobs.Movement _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Movement.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct VitalsSystem : ISystem
    {
        TimelineJobs.Vitals _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Vitals.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }
}
```

`TimelineJobs.Movement` is a struct the generator writes per system. `Create`
builds its query and type handles once in `OnCreate`; `Schedule` only updates
the handles and schedules the job, so no query is built per update and
Entities logs no warning.

`TimelineSimulationGroup` runs in the fixed-step group. `TimelineLibrary` holds
the `View` of the library, the `KindIndex` in `Kinds`, and a `Generation` that
changes on a hot reload. `TimelineStep.Forward` is the step direction.

### Step 7: authoring components

Each `MonoBehaviour` needs its own file, named like the class. Unity cannot
find the script of a component whose class sits in a file with another name,
and the SubScene shows "The referenced script is missing".

`WalkerAuthoring` bakes the columns the systems need. A baker lives next to it.

File: `Assets/Demo/WalkerAuthoring.cs`

```csharp
using Unity.Entities;
using UnityEngine;

namespace Demo
{
    public sealed class WalkerAuthoring : MonoBehaviour
    {
        public float WalkStat = 1f;
        public int Health = 100;
    }

    public sealed class WalkerBaker : Baker<WalkerAuthoring>
    {
        public override void Bake(WalkerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent<Movement_MotionData>(entity);
            AddComponent(entity, new Movement_WalkStatData { Value = new WalkStat { Value = authoring.WalkStat } });
            AddComponent(entity, new Vitals_HealthData { Value = new Health { Value = authoring.Health } });
            AddComponent<Vitals_CuesData>(entity);
        }
    }
}
```

A timeline reaches its own kinds. A kind that only game code pulses has no
timeline on that entity, so name it with `[RequiresKinds]` on a component of
the same GameObject. A renamed kind is then a compile error.

File: `Assets/Demo/DamageableAuthoring.cs`

```csharp
using Tl.Unity;
using UnityEngine;

namespace Demo
{
    [RequiresKinds(typeof(Damage))]
    public sealed class DamageableAuthoring : MonoBehaviour
    {
    }
}
```

The library bytes are handed over once, at startup, from a `TextAsset` you
drag in the Inspector.

File: `Assets/Demo/LibraryAuthoring.cs`

```csharp
using Tl.Unity;
using UnityEngine;

namespace Demo
{
    public sealed class LibraryAuthoring : MonoBehaviour
    {
        public TextAsset Library;

        void Awake()
        {
            TimelineLibraryBootstrap.Use("game.bytes", Library.bytes);
        }

        void OnDestroy()
        {
            TimelineLibraryBootstrap.Release();
        }
    }
}
```

`TimelineLibraryBootstrap.Use(path, bytes)` stores the bytes.
`TimelineLibrarySystem` loads them into every world that runs and creates the
`TimelineLibrary` and `TimelineStep` singletons. Call `Use` again in every Play
session: the bytes are forgotten when a session starts. In the Editor, when
`path` is a real file the library reloads when that file changes. Here the
path is a label only.

### Step 8: scene and SubScene

1. Make a scene, `Assets/Demo/Demo.unity`.
2. `GameObject > New Sub Scene > Empty Scene...`, save it as `DemoContent`.
   Everything with a Timeline Player must live in a SubScene: baking only runs
   there.
3. In the SubScene add a GameObject `Walker` with `WalkerAuthoring` and
   `Timeline Player`. On the Timeline Player:
   - Library: drag `DemoLibrary` into the field.
   - Start With: one element, `Run`.
   - May Start: two elements, `Dash` and `Stomp`.
4. In the SubScene add a GameObject `Dummy` with `WalkerAuthoring`,
   `DamageableAuthoring` and `Timeline Player` (Library: `DemoLibrary`; leave
   the lists empty).
5. In the main scene (outside the SubScene) add a GameObject `Library` with
   `LibraryAuthoring`. Drag `Timelines/game.bytes` into its Library field.

Names in Start With, May Start and Kinds are the constant names in
`Timelines.g.cs` and kind type names. A wrong name is a baking error that
starts `Tl baking: GameObject '<name>'` (see section 9).

Each entity gets exactly the kinds it can reach. `Walker` carries
`Locomotion`, `Damage` and `Cue` (through `Dash` and `Stomp`). `Dummy` carries
only `Damage` (through `[RequiresKinds]`).

### Step 9: start, stop and pulse from code; rewind

One more file drives the demo from a Burst system. It is the pattern to copy
for your own game: write to the `TimelineRequest` buffer, write pulses, set
`TimelineStep`. It needs `Timelines`, the ids class from step 5, so add it
now.

File: `Assets/Demo/DemoDirector.cs`

```csharp
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Demo
{
    public struct DemoClock : IComponentData
    {
        public int Step;
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateBefore(typeof(TimelineKindTickSystem))]
    public partial struct DemoDirectorSystem : ISystem
    {
        EntityQuery _runners;
        EntityQuery _damageable;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineKindTick>();
            state.RequireForUpdate<TimelineStep>();
            _runners = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineRequest>().WithAll<LocomotionEntry>().Build(ref state);
            _damageable = new EntityQueryBuilder(Allocator.Temp).WithAll<DamageEntry>().Build(ref state);
            state.EntityManager.CreateSingleton(new DemoClock());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var step = SystemAPI.GetSingleton<DemoClock>().Step;
            if (step == 3)
                Start(ref state, Timelines.Dash);
            if (step == 6)
                Start(ref state, Timelines.Stomp);
            if (step == 12)
                Hurt(ref state, 7);
            if (step == 40)
                SystemAPI.SetSingleton(new TimelineStep { Forward = false });
            if (step == 50)
                SystemAPI.SetSingleton(new TimelineStep { Forward = true });
            SystemAPI.GetSingletonRW<DemoClock>().ValueRW.Step = step + 1;
        }

        void Start(ref SystemState state, ushort timeline)
        {
            var runners = _runners.ToEntityArray(Allocator.Temp);
            foreach (var runner in runners)
                SystemAPI.GetBuffer<TimelineRequest>(runner).Add(TimelineRequest.Start(0, timeline));
        }

        void Hurt(ref SystemState state, int amount)
        {
            var sender = SystemAPI.GetSingleton<TimelineKindTick>().BeginPulses(1);
            var writer = sender.Writer;
            writer.Begin(0);
            var targets = _damageable.ToEntityArray(Allocator.Temp);
            foreach (var target in targets)
                DamageKind.Pulses(writer).To(target, amount);
            writer.End();
        }
    }
}
```

What it does, by step:

- Step 3: starts `Dash` on every entity with `Locomotion`. The request goes
  into the entity's `TimelineRequest` buffer. The first argument is the slot;
  pass 0, as the package tests do. The other requests are
  `TimelineRequest.Stop(0, id)`, `StopChannel(0, channel)` and `StopAll(0)`.
- Step 6: starts `Stomp`. It plays beside `Run` (a different channel).
- Step 12: sends a game pulse of 7 `Damage` to every entity with the kind.
- Steps 40 to 49: `TimelineStep.Forward = false`. Ten backward steps. Step 50
  sets it forward again.

Why it is written this way:

- The queries are built with `EntityQueryBuilder` in `OnCreate`. `SystemAPI`
  queries cannot name `LocomotionEntry` or `DamageEntry`: those types are
  generated code, and `SystemAPI.Query` rejects them (`SGQC001`).
- `GetEntityQuery(params ComponentType[])` is not Burst compatible (`BC1028`);
  the builder with one generic argument per call is.
- The system runs before the Tick, so its requests and pulses are merged in
  the same step. Send pulses before the Tick; they merge during that step.
- `BeginPulses(1)` opens one stream with one writer index. Write with
  `DamageKind.Pulses(writer).To(entity, value)` between `Begin(0)` and
  `End()`. From a job, give each job its own index
  (`BeginPulses(jobCount)`), call `Begin(index)` and `End()` inside the job,
  and pass the job's handle to `TimelineKindTick.AddPulseWriter`.
- A pulse to an entity without the kind, and a start for a timeline the entity
  cannot reach, are dropped and counted: `TimelineKindTick.DroppedPulses` and
  `DroppedStarts`. A count above zero usually means a missing
  `[RequiresKinds]`, Kinds entry or May Start name.

### Step 10: run it

Press Play on `Demo.unity`. The SubScene streams in, the library loads, and
the director runs its script. To look at the entities, open `Window > Entities
> Hierarchy` and select `Walker` in the runtime world (not checked in this
guide).

The checked result is the test in section 7, run headless in a fresh project
on 7000.0.0a16 (1 passed). After step 70 it asserts:

| What | Value |
|---|---|
| `Walker` health | 58 (100 − 25 − 10 from `Stomp` − 7 from the game pulse) |
| `Dummy` health | 93 (the game pulse only) |
| `Walker` cues | `Count` 2, `Last` 2 |
| `X` at step 50 equals `X` at step 30 | ten steps back undo ten steps forward |
| `DroppedPulses`, `DroppedStarts` | 0 and 0 |

The same demo built as a Windows player with `BuildPipeline.BuildPlayer`
(scene `Demo.unity`, package from a local path) gives these values in both a
CoreCLR and an IL2CPP player run with `-batchmode -nographics`, with no
warning in the player log (7000.0.0a16).

## 4. Reading other entities

A feeder gets columns of its own entity as arguments. To read another entity,
put a read-only `ComponentLookup` in a context struct, mark the context as a
system field, and pass it to the generated job. This is the pattern in the
package's test game (`Tests/Kinds/Game/KindGame.cs`, `Harness/KindWorld.cs`).

Types:

```csharp
public struct Target { public Entity Value; }
public struct Anchor : IComponentData { public float Boost; }

public struct AnchorLookup
{
    [ReadOnly] public ComponentLookup<Anchor> Anchors;

    public float Boost(in Target target) => Anchors.TryGetComponent(target.Value, out var anchor) ? anchor.Boost : 1f;
}
```

The feeder takes the target column and the lookup. The system declares both:

```csharp
public struct Walk : ITrack<WalkTrack, WalkClip>
{
    [Feeds(typeof(Locomotion))]
    public static Speed Evaluate(in Frame<WalkTrack, WalkClip> f, in WalkStat stat, in Target target, in AnchorLookup anchors)
        => new Speed { Value = f.Direction * f.Clip.Speed * f.Track.Scale * stat.Value * anchors.Boost(in target), Priority = f.Clip.Priority };
}

[TimelineSystem]
public ref partial struct Movement
{
    public Span<Motion> Motion;
    public ReadOnlySpan<WalkStat> WalkStat;
    public ReadOnlySpan<Target> Target;
    public AnchorLookup Anchors;
}
```

Any field that is not a `Span` is a context value passed to the job. The
generated entry point gets one more parameter, and the system keeps the lookup
as a field and updates it each step:

```csharp
[BurstCompile]
[UpdateInGroup(typeof(TimelineSimulationGroup))]
[UpdateAfter(typeof(TimelineKindTickSystem))]
public partial struct MovementSystem : ISystem
{
    ComponentLookup<Anchor> _anchors;
    TimelineJobs.Movement _jobs;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<TimelineLibrary>();
        state.RequireForUpdate<TimelineStep>();
        _anchors = state.GetComponentLookup<Anchor>(true);
        _jobs = TimelineJobs.Movement.Create(ref state);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var library = SystemAPI.GetSingleton<TimelineLibrary>();
        var step = SystemAPI.GetSingleton<TimelineStep>();
        _anchors.Update(ref state);
        var anchors = new AnchorLookup { Anchors = _anchors };
        state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, anchors, state.Dependency);
    }
}
```

The baker adds the new column: `AddComponent<Movement_TargetData>(entity);`.
The generated `Schedule` changes with the system when Unity recompiles.

## 5. Presentation events

Two ways to tell the presentation layer that something happened.

Events with `Events<T>`. A consumer method raises an event. On Unity the
generated job runs it in Burst and writes the events into a
`DynamicBuffer<TimelineEvent<T>>` on the entity that owns the timeline:

```csharp
public struct Whistle : ITrack<CueTrack, CueClip>
{
    public static void OnClipStart(in Frame<CueTrack, CueClip> f, ref Events<Cue> cues)
    {
        if (f.Direction > 0)
            cues.Raise(new Cue { Voice = f.Clip.Voice });
    }
}
```

The `[TimelineSystem]` struct that the method belongs to declares a field
`public Events<Cue> Cues;`. The Timeline Player bake adds the buffer to every
entity for each event type the game declares. Each step clears the buffer and
refills it with the events raised in that step, ordered by playback slot, then
by raise order. `Record.Value` is your struct; `Record.Slot` and
`Record.Sequence` give the order. Read the buffer in a system that runs after
`TimelineSimulationGroup`. The rules for when `OnClipStart`, `OnClipEnd` and
`ExecuteActive` fire are in [KINDS.md](KINDS.md), "Presentation events". The
demo in this guide does not use `Events<T>`, so this walkthrough did not run it.

Cues through a kind. The demo uses a pulse kind in `Each` mode that writes a
cue column. It needs no extra buffer:

```csharp
[TimelineKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
public struct Cue
{
    public static void Apply(in int id, ref CueLog log)
    {
        if (id == 0)
            return;
        log.Count++;
        log.Last = id;
    }
}
```

`Each` applies every pulse exactly once, in a fixed order, instead of
combining them. The clip data carries the cue id (`CueId` in `HitClip`), and
`Hit.Announce` pulses it at clip start. Backward it returns 0, which `Apply`
ignores, so rewinding does not play the cue again.

Your presentation system compares `CueLog.Count` with the count it saw last
frame and reacts to the difference. `Last` is the id of the newest cue.

## 6. Run it against the same hash

Every host of Tl is checked with one state hash: a fixed script of starts,
stops and pulses over 600 steps, and a hash of the columns after each step.
`KindScript` in `Tests/Kinds/Harness` is that script; the pinned value
`13943614770702343869` is equal in the .NET run, the Unity Editor and the IL2CPP
players (see [KINDS.md](KINDS.md)).

To check your own game the same way, hash the columns after each step. This is
the mix function from `KindScript`:

```csharp
static ulong Mix(ulong h, float v) => (h ^ (ulong)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;

static ulong MixInt(ulong h, int v) => (h ^ (ulong)(uint)v) * 1099511628211UL;
```

Start from `14695981039346656037UL`, mix every column of every row after each
step, and pin the result. A change you did not expect shows up as a different
number.

## 7. Test it

The repository's own tests are described in [KINDS.md](KINDS.md). To test
your game, use an assembly with a test reference:

File: `Assets/Demo/Tests/Demo.Tests.asmdef`

```json
{
  "name": "Demo.Tests",
  "references": [ "Demo", "Tl.Unity", "Unity.Entities", "Unity.Collections", "UnityEngine.TestRunner", "UnityEditor.TestRunner" ],
  "overrideReferences": true,
  "precompiledReferences": [ "nunit.framework.dll" ],
  "defineConstraints": [ "UNITY_INCLUDE_TESTS" ],
  "autoReferenced": false
}
```

This PlayMode test loads the scene and plays the whole script in the default
world. A hand-built `World` skips the auto-created systems, the group order and
the singletons, so run at least one test through a scene.

File: `Assets/Demo/Tests/DemoTests.cs`

```csharp
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Demo.Tests
{
    public class DemoPlayTests
    {
        [UnityTest]
        public IEnumerator TheDemoRunsInTheDefaultWorld()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Demo/Demo.unity", new LoadSceneParameters(LoadSceneMode.Single));
            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var clockQuery = entities.CreateEntityQuery(typeof(DemoClock));
            using var walkers = entities.CreateEntityQuery(typeof(LocomotionEntry), typeof(Vitals_HealthData));
            using var dummies = entities.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Vitals_HealthData>() }, None = new[] { ComponentType.ReadOnly<LocomotionEntry>() } });
            var seen = new Dictionary<int, float>();
            var deadline = Time.realtimeSinceStartup + 120f;
            var step = 0;
            while (Time.realtimeSinceStartup < deadline && step < 70)
            {
                yield return null;
                if (clockQuery.IsEmpty || walkers.IsEmpty)
                    continue;
                step = clockQuery.GetSingleton<DemoClock>().Step;
                using var found = walkers.ToEntityArray(Allocator.Temp);
                seen[step] = entities.GetComponentData<Movement_MotionData>(found[0]).Value.X;
            }
            Assert.GreaterOrEqual(step, 70, "the director reached step 70");
            using var w = walkers.ToEntityArray(Allocator.Temp);
            using var d = dummies.ToEntityArray(Allocator.Temp);
            Assert.AreEqual(1, w.Length);
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(100 - 25 - 10 - 7, entities.GetComponentData<Vitals_HealthData>(w[0]).Value.Value, "stomp hits and one game pulse");
            Assert.AreEqual(100 - 7, entities.GetComponentData<Vitals_HealthData>(d[0]).Value.Value, "game pulse only");
            var cues = entities.GetComponentData<Vitals_CuesData>(w[0]).Value;
            Assert.AreEqual(2, cues.Count);
            Assert.AreEqual(2, cues.Last);
            Assert.Greater(entities.GetComponentData<Movement_MotionData>(w[0]).Value.X, 0f);
            if (seen.TryGetValue(30, out var before) && seen.TryGetValue(50, out var after))
                Assert.AreEqual(before, after, "ten steps back return X to its value ten steps earlier");
            else
                Assert.Fail("steps 30 and 50 were not both observed");
            using var tickQuery = entities.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<TimelineKindTick>() }, Options = EntityQueryOptions.IncludeSystems });
            var tick = tickQuery.GetSingleton<TimelineKindTick>();
            Assert.AreEqual(0, tick.DroppedPulses);
            Assert.AreEqual(0, tick.DroppedStarts);
        }
    }
}
#endif
```

Run it from `Window > General > Test Runner` (PlayMode), or headless:

```sh
Unity -batchmode -nographics -projectPath <Project> -runTests -testPlatform PlayMode \
  -testResults <abs path>/results.xml -logFile <abs path>/run.log
```

Rules for headless runs:

- Do not pass `-quit` together with `-runTests`.
- Use absolute paths for `-testResults` and `-logFile`.
- Proof is `error CS` = 0 in the log and the `<test-run total passed failed>`
  counts in the XML. An exit code of 0 alone is not proof.
- Wait on wall-clock time (`Time.realtimeSinceStartup`), not on frames:
  frames are about 1 ms in batch mode, and SubScenes stream in asynchronously.
- The step system lives on the entity of the system, so a query for
  `TimelineKindTick` needs `EntityQueryOptions.IncludeSystems`.

EditMode tests of baking (the package ones are in
`Tests/Kinds/EditorTests`) bake GameObjects without a scene and need `TL_TLB`
for the capacity bake. To run the whole package suite the way a user installs
it, from a clone of `tl.unity`:

```sh
TL_TLB=<tl>/tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll bash eng/test-com-tl-unity.sh
```

It builds a Unity project whose only Tl dependency is the package and runs
EditMode and PlayMode (on Windows, under Git Bash). Without `TL_TLB` the
capacity-bake test is skipped.

## 8. Tools and sizing

| Tool | What it does |
|---|---|
| `Tools > Tl > Kind Report` | Logs each kind archetype per world: entities, chunks, chunk capacity and kind bytes per entity. Warns about more than 32 kinds, more than 1024 B of kind storage, and fewer than 16 entities per chunk. Open a scene with a SubScene or enter Play mode first. |
| `Tools > Tl > Bake Kind Capacities` | Runs `tlb kinds` for each library with a Kinds File and rewrites `Kinds.g.cs` only when a capacity changes. Also runs after each script reload and when a `.json` in the library folder is imported. |
| `Assets > Create > Tl > Kind Profile` | A named list of kinds. With Kind Sets set to `Union Per Profile` on the library, entities with the same profile share one archetype. |
| `[RequiresKinds(typeof(K))]` | Names kinds that only game code pulses. See step 7. |

The report logic is in the runtime assembly: `KindReport.Capture(entityManager,
KindRegistry.Discover())` returns the rows, `KindReport.Warnings(rows)` the
warnings.

### Sizing rules

- Every entity pays a header plus inline entries for each kind it can reach.
  The baker gives an entity exactly the kinds it reaches (Kind Sets `Exact`),
  which is the least memory and the most archetypes. A profile trades bytes
  for fewer archetypes.
- A kind's inline capacity is `KindCapacity.<Kind>` in `Kinds.g.cs`. `tlb kinds`
  computes it as the worst case on one entity (span entries, timeline pulses,
  plus `gamePulses`), capped by `maxInlineBytes`. `inline` sets it directly.
  Entry bytes are 8 for a span-only kind, else 8 + the value size rounded up to
  4. In the demo, the report says: `Cue` capacity 3 (36 B), `Damage` 5 (60 B),
  `Locomotion` 2 (16 B).
- When more entries arrive than the inline capacity, the buffer moves to the
  heap for that entity. Raise `gamePulses` or `inline` for kinds that get many
  game pulses.
- Rules of thumb: tens of kinds, not hundreds; capacity 0 for rare kinds; let
  the Kind Report guide the rest. Full rules: [KINDS.md](KINDS.md).

### Speed

Measured on Unity 7000.0.0a16 in the Editor with Burst on and Editor safety
checks on, 100,000 entities that each carry all four kinds of the package test
game (`Tests/Kinds/Bench/KindBench.cs`). The numbers are from the comment on
`tl.unity` issue #8; the comment does not name the machine.

| Shape | Time per entity per step |
|---|---|
| A: every entity plays one walk clip | 8.0 ns |
| B: 10% of entities play | 1.9 ns |
| B0: none playing | 0.6 ns |

Allocation per warm step: 0 B. With Editor Burst safety checks off, B was 1.3 to
1.4 ns. Your clip code costs what it costs; these numbers are the timeline
plumbing only. A comparison with another design is in
[COMPARISON.md](COMPARISON.md).

## 9. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Old behaviour after a code change | One compile error anywhere keeps the old assemblies loaded. Check the log for `error CS` before trusting a run. The Console can be empty while `Editor.log` has the errors. |
| SubScene serves old data after you rebuilt `game.tlib` or changed a baker | Stale bake. Reimport the SubScene (right-click the SubScene asset, `Reimport`), then check one baked component exists in the live world. The baker depends on the Library asset, the Profile and `game.tlib`, not on your baker code. |
| `Unknown Type: Demo.LocomotionEntry` (or `Movement_MotionData`) at run time, with 0 `error CS` | The generated registration did not run. Check that the game asmdef references `Tl.Unity.Generate` and that the `Unity.Tl.CodeGen` post-processor compiled (no `error` for it in `Editor.log`). Do not check generated `.g.cs` files into an assembly that also references `Tl.Unity.Generate`. |
| `error CS0103` for `Movement_MotionData`, `TimelineJobs`, `LocomotionEntry` | The generator did not run. Check that the game asmdef references `Tl.Unity.Generate`, and fix any other `error CS` first: the generated code is not refreshed while the assembly fails to compile. |
| `error CS0101` or `CS0229` for `KindCapacity` | Two `Kinds.g.cs` files. Bake the library with `--kinds` (step 5) and delete the one next to `game.tlib`. The Timeline Bridge passes the library's Kinds File to `tlb`, so it writes no second file. |
| `SGQC001: WithAll<LocomotionEntry>() is not supported` | `SystemAPI.Query` cannot name generated types. Build the query with `EntityQueryBuilder` in `OnCreate`. |
| `BC1028: Creating a managed array ComponentType[]` | `GetEntityQuery(params ...)` is not Burst compatible. Use `EntityQueryBuilder` with one generic call per type. |
| `BC1042` and a managed static | Something on the step path reads a managed static. Read `TimelineLibrary`, `TimelineStep` and `TimelineKindTick` from singletons instead. |
| `The referenced script is missing on <GameObject>` | The authoring `MonoBehaviour` is not alone in a file named like the class. One class per file. |
| `Tl baking: GameObject '<name>' has no Library.` and similar | Set the Library field on the Timeline Player. Other messages and fixes: [KINDS.md](KINDS.md), "Step 5: read baking errors". |
| `... but has no 'Movement_MotionData' component.` | The GameObject reaches a kind whose system needs that column. Add the baker that adds the column to the same GameObject. |
| `TimelinePlayerAuthoring` names a timeline Unity does not find | Names are the PascalCase constants in the ids class (`run.json` gives `Run`, `potion_hop.json` gives `PotionHop`). |
| Nothing moves; no errors | The library was not handed over. `LibraryAuthoring` must exist in the main scene with its TextAsset set, and run before the first step. A SubScene loads asynchronously, so wait on wall-clock time in tests. |
| A start or pulse has no effect | Read `DroppedStarts` and `DroppedPulses` on `TimelineKindTick`. Above zero means the entity lacks the kind: add `[RequiresKinds]`, a Kinds entry or a May Start name. |
| `Kind 'X' is registered twice: by assembly 'A' and by assembly 'B'.` | Kind names (type names) must be unique across all assemblies. Rename one. |
| `Script assembly '<name>' is not compiled yet.` from the capacity bake | Fix Game Assembly on the library asset, or wait for the compile. |
| `could not run '<tlb>'` | Fix the Tlb field: the full path of `Tl.Bake.dll`, or a `tlb` on `PATH`. |
| `Referenced assembly file not found` from `tlb library` | Run `Bake Kind Capacities` first in the open Editor so `Temp/TlKindBake` exists. It is removed when the Editor closes. |
| `UnityEngine.ManagedKernelModule` could not be loaded from `tlb library` | You pointed `--assembly` at `Library/ScriptAssemblies`. Use `Temp/TlKindBake`, which also has the engine assemblies. |
| `TLUNITY9103` warning | `KindCapacity.<Kind>` does not exist yet. Run `Bake Kind Capacities`; until then capacity falls back to 0. |
| `TLUNITY9105` warning | A consumer declares a legacy method that Unity does not dispatch (a marker handler, or `OnClipStart`, `OnClipEnd` or `ExecuteActive` with parameters other than `Events<T>`). Feed a kind instead. |
| Strange state after leaving Play mode with Domain Reload off | Statics survive between sessions. `TimelineLibraryBootstrap` resets its own at `SubsystemRegistration` and forgets the bytes, so call `Use` in every session (`LibraryAuthoring.Awake` does). Reset your own statics the same way and mark the type `[NoAutoStaticsCleanup]` under `#if UNITY_7000_0_OR_NEWER`. |
| `Unity.Entities` binds to your namespace | A namespace segment named `Unity` or `Tl` in your code captures `Unity.X`. Write `global::Unity.Entities...` or rename the namespace. |
| A system throws every frame | `GetSingleton<T>()` without `RequireForUpdate<T>()` in `OnCreate`. |
| A query catches entities you did not mean | To exclude an enableable component use `Absent`; `None` also matches entities where it is present but disabled. |
| Test says `Enter play mode before calling any LoadSceneInPlayMode functions` | A test that loads a scene in Play mode must be in an assembly that runs in PlayMode (no `includePlatforms` restriction), not an Editor-only assembly. |
| Test finds 0 tests | The test assembly needs `UNITY_INCLUDE_TESTS`, `UnityEngine.TestRunner`, `nunit.framework.dll` and `overrideReferences`. |
| Allocation assert reads 48 B | `GC.GetAllocatedBytesForCurrentThread()` boxes its arguments in the second read. Store the delta in a local first, then assert on it. |

## 10. Known gaps

- `tlb library` needs `Temp/TlKindBake`, which exists only while the Editor is
  open after a capacity bake. There is no menu command for `tlb library`; the
  Timeline Bridge ([TIMELINE-BRIDGE.md](TIMELINE-BRIDGE.md)) runs it for you.
- The Unity CI jobs do not run on Linux until a runner is started:
  [IAFahim/tl.unity#17](https://github.com/IAFahim/tl.unity/issues/17).
- Not verified: the `GameObject > New Sub Scene` menu (a script built the
  scenes), player builds of this demo (the package's IL2CPP samples are),
  other platforms, and other Unity versions.

## 11. Cheat sheet

Order of work, first time:

1. Manifest: `"com.tl.unity": "https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"`.
2. Build `tlb` (`dotnet build tools/Tl.Bake -c Release` in the `tl` repo).
3. Write `Kinds.cs`: columns, kinds, track and clip structs, feeders,
   `[TimelineSystem]` structs. Asmdef: unsafe on, Tl assemblies and
   `Tl.Unity.Generate` referenced.
4. Write `library.json` and the timeline json files.
5. `Create > Tl > Timeline Library`; fill Folder, Timeline Ids, Game Assembly,
   Namespace, Kinds File, Tlb.
6. `Tools > Tl > Bake Kind Capacities` -> `Kinds.g.cs`.
7. `tlb library <folder> --assembly Temp/TlKindBake/<Game>.dll --out game.tlib
   --ids Timelines.g.cs --kinds Kinds.g.cs --namespace <Ns>`; copy
   `game.tlib` to `game.bytes`.
8. Write the `ISystem`s, bakers and `LibraryAuthoring`.
9. SubScene + Timeline Player (drag the Library asset) + `LibraryAuthoring`
   (drag `game.bytes`). Play.

Every change after that:

| You changed | Do |
|---|---|
| A kind, column, feeder signature or `[TimelineSystem]` | Compile. The generator rewrites the glue. |
| A timeline json | Reimport; `tlb library`; copy `game.bytes`. Capacity bake runs by itself. |
| `library.json` kinds section | Bake Kind Capacities. |
| A clip or track struct field | `tlb library` again (layout fingerprint check), copy `game.bytes`. |

API on one page:

| Need | Code |
|---|---|
| Hand over the library | `TimelineLibraryBootstrap.Use(path, bytes)`; `Release()` |
| Start a timeline | `buffer.Add(TimelineRequest.Start(0, Timelines.Dash))` (optional third argument: start tick) |
| Stop it | `TimelineRequest.Stop(0, id)`, `StopChannel(0, channel)`, `StopAll(0)` |
| Game pulse | `var s = tick.BeginPulses(1); var w = s.Writer; w.Begin(0); DamageKind.Pulses(w).To(entity, v); w.End();` |
| Rewind | `SystemAPI.SetSingleton(new TimelineStep { Forward = false })` |
| Schedule a system | `_jobs = TimelineJobs.Movement.Create(ref state)` in `OnCreate`, `_jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency)` in `OnUpdate` |
| Read other entities | `ComponentLookup` in a context struct on the `[TimelineSystem]`, passed to the job |
| Presentation event | `Events<T>` read from `DynamicBuffer<TimelineEvent<T>>` after the group, or an `Each`-mode pulse kind that writes a cue column |
| Dropped counts | `TimelineKindTick.DroppedPulses`, `DroppedStarts` |
| Memory report | `Tools > Tl > Kind Report` |
| Run the suite | `bash eng/test-com-tl-unity.sh` with `TL_TLB` set |

Order inside a step: `Requests -> Tick -> your systems -> Drain`. Send pulses
and requests before the Tick (`[UpdateBefore(typeof(TimelineKindTickSystem))]`);
schedule kind jobs after it (`[UpdateAfter(typeof(TimelineKindTickSystem))]`).
