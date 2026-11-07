# Engine-agnostic Tl: one data set, one hash, any host

Plan only. Nothing here is built yet. Every new name is marked **(proposed)**; every other name was read in the code on
IAFahim/tl `f07c61c5` and IAFahim/tl.unity `9328184` (both `main`, 2026-11-08).

The owner's goal, in one line: Tl bakes **our** data to a JSON + TLB pair; a .NET command line program runs it; Unity runs the same data and
prints the same hash. Kind packs (Force, Animation, Inventory, Stats, more) build outside Unity. Unity keeps its own baking and blobs and
the zero-config Timeline experience. Speed stays where it is.

## 1. Where the code sits today (measured)

How it was counted: a script walked every `.cs` file and marked it Unity-bound when it has a `using UnityEngine`/`UnityEditor`/`Unity.*`
line or a `global::Unity*` name. Then the Unity package's runtime files were sorted by hand into the buckets below, by reading each file's
types and sampling their bodies. Line counts are measured; the "rule logic" bucket is my judgement of which files hold rules, so treat its
size as an estimate of plus or minus 300 lines.

### 1.1 tl (public core)

| Part | Files | Lines | Unity-bound |
|---|---|---|---|
| `src/Tl.Core` (runtime: model, playback, requests, kinds, library view) | 29 | 4,722 | 0 |
| `src/Tl.Gen.CSharp` (plain generator) | 20 | 3,808 | 0 (one 9-line `UnityJobEmitter` that writes strings) |
| `tools/Tl.Gen.Tlb` (the packer behind `tlb`) | 20 | 6,303 | 0 |
| `tools/Tl.Bake` (the `tlb` CLI: bake, `library`, `kinds`, `--watch`, `--inspect`) | 6 | 1,288 | 0 |
| tests, benchmarks, samples, other tools | | 53,150 | 0 |

tl is fully engine-free: 13 projects, no Unity reference anywhere.

### 1.2 tl.unity package (`com.tl.unity`, without tests and samples: 18,498 lines)

| Bucket | Files | Lines | Engine-free? | Where it should live |
|---|---|---|---|---|
| `Core/`: copy of `Tl.Core` (synced by `eng/sync-tl-core.sh`) | 30 | 4,728 | yes | stays a copy of tl |
| `Editor/Bake/Tlb/`: copy of `Tl.Gen.Tlb` (synced by `eng/sync-tl-bake.sh`) | 18 | 6,031 | yes | stays a copy of tl |
| Engine-free code written only in tl.unity: `TlLibraryAssembler`, `TlKindPayload`, `TlBake`, `TlKindBytes`, and the coder API `TlClipAttribute`, `TlKindAttribute`, `ITlTrack`, `TlNoSettings`, `TlFrame`, `TlCurve` | 9 | 664 | yes, but trapped in Unity assemblies | **move to Tl.Core / Tl.Bake** |
| Playback and kind rules rewritten in Unity form: `TimelineKernel`, `KindTick` (`KindCursor`), `KindIndex`, `KindPulses`, `TimelinePlayer`, `TimelineEvents`, `TlTimelineSwap` | 7 | 1,335 | no (DynamicBuffer, NativeList) | **rules move to Tl.Core**, thin Unity callers stay |
| Unity runtime glue (systems, components, handles, library register, `TlBaker`, `ITlBaker`) | 28 | 1,968 | no | stays (adapter) |
| Timeline authoring, editor, baker, preview, inspectors, ILPostProcessor | 27 | 1,584 | no | stays (adapter) |
| `Tools~/Tl.Gen.Unity` (Unity generator; links the front end of `Tl.Gen.CSharp`) | 12 | 2,047 | emits Unity code | stays, but `UnityNewStyle.cs` (278 lines, the new-style `[TlKind]`/`TlFrame` rewrite) **moves to the shared front end** |
| plain generator stub, snapshot tool | 3 | 141 | yes | stays (tests) |

Assemblies in the package: 7 (not counting tests and samples). Engine-free: `Tl.Core` and `Tl.Unity.Bake`. Unity-bound: `Tl.Unity`,
`Tl.Unity.Tracks`, `Tl.Unity.Editor`, `Tl.Unity.Tracks.Editor`, `Unity.Tl.CodeGen`.

Of the 7,739 lines tl.unity writes itself (everything but the two copies), about 2,300 (30 %) is engine-free logic or rules that belong
in tl. That is the "too Unity-bound" part.

### 1.3 The three findings that matter most

1. **The coder API lives in a Unity assembly.** Of the Tl names in the five Force files, only `Feeds` is in `Tl.Core`. `TlClip`, `TlKind`,
   `ITlTrack`, `TlFrame` and `TlCurve` sit in `Tl.Unity`, and only the Unity generator understands them (`tl` greps: no hit outside
   `KindCatalog.cs`, which reads `TlKind` by name). So `ForceClip` and `ForceKind` cannot compile in a .NET project today. **Tl defect.**
2. **The playback rules exist three times.** `PlaybackTable` (Core, .NET host), `TimelineKernel` and again inside `KindCursor.Apply`
   (Unity) each implement start, stop, channel replace, pause, loop, hold and next. The pinned hashes agree today, but every rule change has
   to be made three times. **Tl defect.**
3. **Data identity depends on engine things.** `TlTimeline` holds `Unity.Entities.Hash128`; pair keys in TLB are the hash of
   `"Namespace.Type, AssemblyName"` (`Keying.StableName`), so a clip type compiled into `Assembly-CSharp` can only be read by a .NET
   program that has an assembly with that name; and on 7000.0.0a16 `Unity.Mathematics.float3` ships inside the engine
   (`UnityEngine.MathematicsModule.dll`), so a pack that uses `float3` cannot leave Unity. **Tl defects** (the first two), and one owner
   decision (the math type, question 1).

No proposal below needs `SystemBase`, a managed hot path or a workaround. The register path (`TlLibrary` behind a `GCHandle`) is managed but
runs only when blobs arrive, never per frame; it stays as it is.

## 2. Target layering

```
 kind packs (Force, Stats, Inventory, Animation...)   plain C#, depend on Tl.Core only, one source folder
        |                         |
 Tl.Core (engine-free)      Tl.Bake (engine-free, bake time)       data: tl.project.json + timeline JSON + scene JSON  <->  TLB / .tlib
        |                         |
 hosts: .NET CLI `tl`   |   Unity adapter (com.tl.unity)   |   Godot / server / console adapter
```

### 2.1 Tl.Core: everything that decides what happens

Keeps: `LibraryView`, `Frame`, `ITrack`, `IBlend`, kinds (`KindTable`, `KindSectionView`), events, `RequestOp`, `PlaybackFlags`,
`Playback`.

Gains (moved from tl.unity, same names, same namespace `Tl`, so user code does not change):

- the coder API: `TlClipAttribute`, `TlKindAttribute`, `ITlTrack<TClip>`, `TlNoSettings<TClip>`, `TlFrame<TClip>`, `TlCurve`.
- `TlAssetId` **(proposed)**: 16 unmanaged bytes, replaces `Unity.Entities.Hash128` inside `TlTimeline`. Unity converts at the edge.
- `PlaybackRules` **(proposed)**: one unmanaged, static, Burst-safe implementation of every playback rule, written against a small struct
  interface so each host keeps its own storage:

```csharp
namespace Tl
{
    public unsafe interface IPlaybackSlots
    {
        int Length { get; }
        ref Playback this[int index] { get; }
        bool TryAdd(in Playback value);
        void RemoveAt(int index);
    }

    public static unsafe class PlaybackRules
    {
        public static bool Advance(ref Playback slot, in LibraryView view, bool forward, out bool handover, out bool removed);
        public static void Apply<TSlots, TClosed>(ref TSlots slots, in TlRequest request, in LibraryView view, ref TClosed closed)
            where TSlots : struct, IPlaybackSlots where TClosed : struct, IClosedSink;
    }
}
```

  `.NET`: `PlaybackTable` wraps its native rows in a struct that implements `IPlaybackSlots`. Unity: a struct over
  `DynamicBuffer<Playback>` implements it. Both the .NET JIT and Burst compile one specialised copy per struct, so there is no interface
  call in the machine code. Burst already compiles Core code today (`KindTickJob` calls `LibraryViewReader.StageAt`).
- `KindRules` **(proposed)**: the per-row kind entry rules now in `KindCursor` and `KindStore.TickPass` (insert, remove, pulse start, end,
  marker, purge and rebuild at a tick), over `IKindRows` **(proposed)** the same way. The chunk loop stays in each host: Unity keeps its
  `IJobChunk`, .NET keeps its array loop. Only the per-row logic is shared.
- `TlRequest` **(proposed)**: the one request record (op, timeline id or asset, slot, tick, value). `RequestOp` gains `Seek`, `SetSpeed`,
  `SetSpeedChannel`, `PauseChannel`, `ResumeChannel` **(proposed)**.
- `LibraryAssembler` **(proposed)**: today's `TlLibraryAssembler` (Unity runtime, 309 lines, its own copy of the TLB and library byte
  constants) moves here. `tlb library` and Unity's register system both call it. One assembler, one set of constants.
- `TlStateHash` **(proposed)**: the state hash both hosts print (section 3).
- `Tl.Mathematics` **(proposed, question 1)**: `float2/3/4`, `quaternion` with lowercase `x, y, z, w` fields, so their layout digest
  (`PairLayoutDigest` folds field names and widths) equals Unity's. Plain structs, Burst-friendly.

### 2.2 Data format

- **Timeline JSON** stays the format `tlb` reads today (`name, duration, next, channel, tracks[], clips[] {type, start, end, data}`), with
  a `"schema": 1` field added **(proposed)** and two gaps closed: nested structs and fixed buffers (`TlCurve` as a 16-number array), which
  the JSON path cannot write today (zero-config-arch 4.1).
- **Project JSON** `tl.project.json` **(proposed)**: schema, tick rate, channels, kind knobs (`Inline`, `GamePulses`), the packs used with
  their version and layout digest, the timeline list. Replaces `library.json` (a superset of it).
- **Scene JSON** `*.scene.json` **(proposed)**: entities in a fixed order, the state values each starts with, what each plays on start,
  and an optional request script (`[{ "step": 40, "entity": 2, "op": "Pause", "timeline": "dash" }]`). This is what `tl run` executes.
- **TLB / .tlib** stays byte-identical. JSON → TLB → JSON is a round trip test: `tl bake` then `tl decode` **(proposed, from
  `TlbIntrospection`)** gives back the canonical JSON.
- **One packer.** Per timeline: Unity already builds a `TypedTimeline` from Timeline assets and calls `TlBake.BakeTyped` (the synced
  `Tl.Gen.Tlb`); `tl bake` reads JSON into the same `TypedTimeline` and calls the same `TimelineBaker.BakeTyped`. Per library: both call
  `LibraryAssembler`. The per-timeline kind payload (`TlKindPayload`, today a Unity-only format) becomes part of the documented TLB
  library layout in `tl/docs/library-layout.md`.
- **Pack identity.** `[assembly: TlPack("Tl.Force")]` **(proposed)** sets the name that pair keys use in place of the assembly name.
  Without it nothing changes, so every pinned byte fixture keeps its bytes.

### 2.3 Control API in Tl.Core, no engine types

Today .NET has `PlaybackTable.Start/Stop/StopChannel/StopAll/Pause/Resume(int entity, ...)` and `Requests` (a pointer-based writer);
Unity has `TimelineRequest.Start/...` factories and `TlRequests.Start/Stop/Pause/Resume(this DynamicBuffer<TimelineRequest>, in TlTimeline)`.
Target: one request vocabulary in Core, written into whatever list the host gives.

```csharp
namespace Tl
{
    public interface IRequestSink { void Add(in TlRequest request); }

    public static class TlControl
    {
        public static void Start<T>(ref T sink, in TlTimeline timeline, ushort atTick = 0) where T : struct, IRequestSink;
        public static void Stop<T>(ref T sink, in TlTimeline timeline) where T : struct, IRequestSink;
        public static void Pause<T>(ref T sink, in TlTimeline timeline) where T : struct, IRequestSink;
        public static void Resume<T>(ref T sink, in TlTimeline timeline) where T : struct, IRequestSink;
        public static void Seek<T>(ref T sink, in TlTimeline timeline, ushort tick) where T : struct, IRequestSink;
        public static void SetSpeed<T>(ref T sink, in TlTimeline timeline, TlSpeed speed) where T : struct, IRequestSink;
        public static void StopChannel<T>(ref T sink, byte channel) where T : struct, IRequestSink;
        public static void PauseChannel<T>(ref T sink, byte channel) where T : struct, IRequestSink;
        public static void SetSpeedChannel<T>(ref T sink, byte channel, TlSpeed speed) where T : struct, IRequestSink;
    }
}
```

All names above **(proposed)**. Rewind needs nothing new: the step already takes `forward`, and `PlaybackFlags.Reverse` reverses one
playback. Seek reuses the purge-and-rebuild-at-tick path that live edit uses today (`TlTimelineSwapJob`, `KindCursor.Purge`).

Speed fits in the bytes `Playback` already has. `Playback` is 8 bytes and its last field is `ushort Reserved`. Proposed: `byte Rate`
(0 means 1x, so every existing playback is unchanged; otherwise n/16, so 8 is half speed and 32 is double) and `byte Phase` (the
fraction carried between steps). A playback at 2x crosses two ticks in one step and the kind rules process both ticks' boundaries in order,
so events are never skipped. `Playback` stays 8 bytes.

### 2.4 Kind packs

A pack is one source folder, no engine names in its source:

```
packs/Force/
  Force.cs                 ForceClip, ForceKind, ForceTrack, [TlState] partial struct Force
  Tl.Force.csproj          .NET: references Tl.Core + the Tl.Gen.CSharp analyzer
  package.json             Unity: UPM package, git URL with ?path=/packs/Force
  Tl.Force.asmdef          Unity: references Tl.Core and Tl.Unity (the source does not use them; the generator does)
```

The same `.cs` files compile in both hosts, and each host's generator adds its own glue:

- .NET: `Tl.Gen.CSharp` emits the plain kind loop over arrays (as it does for `benchmarks/KindSystems` today).
- Unity: `Tl.Gen.Unity` (runs on any assembly that references `Tl.Unity`, fact F3) emits `partial struct Force : IComponentData {}` and the
  Burst kind jobs, and the existing ILPostProcessor registers the component (the field-notes rule for generator-made components).

`[TlState]` **(proposed)** marks a pack's state struct. The five Force contract files do not need it: a coder's own `IComponentData`
(Unity-only game code) keeps working as today.

### 2.5 Unity adapter (what stays in com.tl.unity)

Authoring to data (Timeline tracks and mirrors, `TlDirectorBaker`, `TlTimelinePacker`), blobs (`TlTimelineBlob`, `TlTimelineSet`), the
register system, the `ISystem`s and Burst `IJobChunk`s, preview, inspectors, the ILPostProcessor. Each Unity system becomes a thin caller:
it takes chunk data, wraps the buffers in the `IPlaybackSlots` / `IKindRows` structs and calls `PlaybackRules` / `KindRules`.

How the source is shared today: `eng/sync-tl-core.sh` and `eng/sync-tl-bake.sh` copy tl's `src/Tl.Core` and `tools/Tl.Gen.Tlb` from the
`external/tl-kinds` pin into the package; `eng/build-generators.sh` builds `Tl.Gen.Unity.dll` from `Tools~` against the linked front end of
`Tl.Gen.CSharp`. Change:

- keep the sync scripts (they work, they are checked by `--check`), add the moved files to the copy, and add one for packs only if the
  one-folder-two-manifests layout fails its spike (S-B);
- move `UnityNewStyle.cs` into `Tl.Gen.CSharp/Analysis` so both generators read the new-style API from one place;
- the Unity generator learns to read kinds from **referenced** assemblies (metadata) as well as from source, so a game can use a pack
  without recompiling it into its own assembly (spike S-D).

### 2.6 Other hosts

A Godot (C#), server or console adapter needs five things, all small:

1. a tick source (Godot `_PhysicsProcess`, a server loop, a fixed timer) that calls `Step(forward)`;
2. entity rows: an `int` per object, in a stable order;
3. state storage: the plain generator's arrays, or the engine's own (copied in and out once per step);
4. the "apply to the engine" part a coder writes (Godot: set `Node3D.Position` from `Force`, the twin of `ForceSystem`);
5. authoring: import the JSON (zero work), or an editor plug-in that writes it.

Server and console with NativeAOT already work: `benchmarks/KindSystems.Aot` runs 600 steps bit-equal at 0 B.

## 3. The .NET CLI

The `tlb` tool (`tools/Tl.Bake`, package `Tl.Bake`) gets a second command name `tl` **(proposed)**; `tlb` stays as an alias.

```
tl bake tl.project.json --pack packs/Force/bin/Tl.Force.dll -o game.tlib
tl run  demo.scene.json --library game.tlib --pack packs/Force/bin/Tl.Force.dll --steps 600
  hash=13943614770702343869 steps=600 entities=4
tl decode game.tlib -o out/        writes canonical JSON back
```

`TlStateHash` **(proposed)**: FNV-1a 64 (the mix `KindScript` already uses), over every entity in scene order: its playback slots
(timeline, tick, channel, flags), then each `[TlState]` column's raw bytes, packs in `TlPack` name order. Taken after every step, so a
mismatch names the first bad step.

Acceptance (the owner's idea, made exact):

| Host | How it gets the data | Must print |
|---|---|---|
| `tl run` (.NET 10, JIT) | `demo.scene.json` + `game.tlib` | hash H |
| `tl run` NativeAOT | same | H |
| Unity Play (Editor, Burst) | a PlayMode test loads the same scene JSON into a world (`TlLibrary.FromBytes` + `Publish`, rows from JSON) | H |
| Unity Play from authoring | the Force demo SubScene, baked by Unity; `Tl > Export` writes its JSON; `tl run` on that JSON | same H on both sides |
| Unity IL2CPP and CoreCLR players | scene JSON shipped as a TextAsset, a probe logs the hash (as phase 8 did) | H |

The existing proof of this already passes for one fixture: `KindUnityHashTests` (tl, .NET) and `KindScript` (Unity Editor and both
players) print 13943614770702343869 and 6607956939547305625. The plan turns that hand-written pair into a generic one.

## 4. API changes, before and after

All names marked **(proposed)** do not exist yet.

### 4.1 Load a library

```csharp
// before (.NET): bytes only, ids from a generated Timelines.g.cs
using var world = new TimelineWorld(File.ReadAllBytes("game.tlib"));
using var players = new PlaybackTable(entityCapacity: 3, slotsPerEntity: 4);

// before (Unity tests/tools)
var library = TlLibrary.FromBytes(bytes); library.Publish(entityManager);

// after (.NET), proposed
using var project = TlProject.Load("tl.project.json", packs: new[] { typeof(ForceKind).Assembly });
using var world = TlHost.Create(project, entities: 3);
```

`TlProject.Load` **(proposed)** reads the project JSON, bakes or loads the `.tlib`, checks every pack's layout digest. `TlHost`
**(proposed)** is a thin owner of today's `TimelineWorld` + `PlaybackTable` + `KindStore`; the old types stay public.

### 4.2 Control

```csharp
// before (.NET)
players.Start(hero, Timelines.Run);
players.Pause(hero, Timelines.Run);
// before (Unity, Burst job)
requests.Start(in dash);           // DynamicBuffer<TimelineRequest>, TlTimeline field
requests.Pause(in dash);

// after (.NET), proposed
var run = project.Timeline("run");               // TlTimeline, by asset id, no generated constants
world.Requests(hero).Start(in run);
world.Requests(hero).SetSpeed(in run, TlSpeed.Of(0.5f));
world.Requests(hero).Seek(in run, tick: 30);
world.Step(forward: false);                      // rewind one step

// after (Unity, Burst job), proposed: same verbs, same names
requests.Start(in dash);                         // unchanged
requests.SetSpeed(in dash, TlSpeed.Of(2f));
requests.PauseChannel(1);
```

Unity keeps `DynamicBuffer<TimelineRequest>` as the storage; its extension methods become one-liners over `TlControl` with a small
`IRequestSink` struct around the buffer. The Force contract and the Control demo compile unchanged.

### 4.3 Kind pack and registration

```csharp
// before: Unity only (TlClip, TlKind, ITlTrack, TlFrame, TlCurve live in Tl.Unity)
[TlClip("Force")] public struct ForceClip { public float3 Direction; public float Strength; public TlCurve Falloff; }

// after: same text, compiles in .NET and Unity; only the math using changes if question 1 says so
using Tl;
using Tl.Mathematics;                                      // proposed

[assembly: TlPack("Tl.Force")]                             // proposed

[TlClip("Force")]
public struct ForceClip { public float3 Direction; public float Strength; public TlCurve Falloff; }

[TlState]                                                  // proposed
public partial struct Force { public float3 Value; }

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

Registration is generated in both hosts; nobody calls a register method. .NET: the generator emits a module and the host installs it
through today's `TlGenerated.Install(ITlModule)`. Unity: as today, a `TlKindSystem<TJobs>` instance per assembly through
`RegisterGenericSystemType`.

### 4.4 Unity adapter entry

```csharp
// unchanged for designers and the contract: Playable Director in a SubScene, TlDirectorBaker bakes it.
// new, proposed: tests and tools can feed the same JSON the CLI runs
var scene = TlScene.Load(json);                    // proposed, Tl.Core
var entities = TlSceneLoader.Spawn(entityManager, scene, library);   // proposed, Tl.Unity, Editor/tests/tools only
ulong hash = TlStateHashSystem.Read(world);        // proposed, Burst job over rows sorted by TlRow (proposed)
```

`Tl > Export` (exists: `TlExport.Export`, writes `game.tlib`) also writes the project JSON, the timeline JSON and a scene JSON of the open
SubScene **(proposed)**.

## 5. Speed

Owner numbers to keep: A about 8, B about 1.8, B0 about 0.6 ns/entity, 0 B warm. Last measured (phase 7): A 7.8/8.0, B 2.0/2.0, B0 0.6/0.6.
.NET reference (`benchmarks/KindSystems`, K-A): 31.09 ms for 100,000 entities × 32 steps, about 9.7 ns/entity.

| Change | Why it should not cost | Measure before and after |
|---|---|---|
| `PlaybackRules`/`KindRules` over struct interfaces | generic struct parameters are specialised by both the JIT and Burst: same code as today's hand-written loops | Unity K bench A/B/B0/C/E rows; .NET KindSystems K-A to K-F; Burst Inspector of `TimelineAdvanceJob` and `KindTickJob`: no indirect call |
| Chunk loops stay per host | Unity keeps `IJobChunk` and its enable-bit skip (that skip is what makes B0 cheap) | B0 row; empty-world E row |
| `Rate`/`Phase` in `Playback.Reserved` | same 8 bytes; `Rate == 0` is today's path with one extra compare on playing slots only | A row all at 1x; a new A2 row at 2x; B0 must not move |
| New `RequestOp`s | requests are rare and handled before the tick | warm frame 0 B |
| `LibraryAssembler` in Core | runs only when blobs arrive | warm frame 0 B; register time on the KindGame fixture |
| `[TlState]` partial component | same bytes, same type in the job | bench B with the Force pack's `Force` in place of the contract `Force`: same band |
| `Tl.Mathematics.float3` | plain fields like Unity's `float3`; Burst vectorises either | spike S-C: `ApplyForce` with each type, 100,000 entities, same ns |
| `TlStateHash` | tests and CLI only, never per frame in a game | none in the bench |

Gate for every phase: both pinned hashes unchanged, A/B/B0 inside their bands on two runs (field notes: first run is cold, plus or minus
0.5 ns is noise), 0 B warm.

## 6. Migration, in order

Sizes are agent-days on this machine, estimated.

| Phase | Work | Size | Depends on | Parallel with |
|---|---|---|---|---|
| 0 Spikes | S-A generator-made `IComponentData` on a `partial` pack struct, registered by the ILPP, used by a Burst job. S-B one folder with `.csproj` and `package.json` (build output kept out of Unity's import, e.g. `bin~`). S-C `Tl.Mathematics.float3` vs Unity `float3` in Burst. S-D Unity generator reads kinds from a referenced assembly | 1 each | - | all four |
| 1 Coder API to Core | move `TlClip`, `TlKind`, `ITlTrack`, `TlNoSettings`, `TlFrame`, `TlCurve` to tl `Tl.Core`; `TlAssetId`; resync | 1 | - | 0 |
| 2 One front end | `UnityNewStyle` into `Tl.Gen.CSharp/Analysis`; plain generator compiles the Force files | 2 | 1 | 3, 4 |
| 3 One set of rules | `PlaybackRules`, `KindRules`; `PlaybackTable`, `KindStore`, `TimelineKernel`, `KindCursor` call them | 4 | 1 | 2, 4 |
| 4 One assembler | `LibraryAssembler` + kind payload layout into Core; `tlb library` and Unity use it | 2 | 1 | 2, 3 |
| 5 Data format v1 | `schema` field, project and scene JSON, nested structs and `TlCurve` in JSON, `tl decode`, round-trip tests, `Tl > Export` writes JSON | 3 | 4 | 6 (control) |
| 6 Control | `TlRequest`, `TlControl`, `Seek`, `SetSpeed`, channel verbs, `Rate`/`Phase` | 3 | 3 | 5 |
| 7 CLI and hash | `tl bake/run/decode`, `TlStateHash`, Unity scene loader and hash test, IL2CPP/CoreCLR player probes | 3 | 2, 5 | 8 |
| 8 Packs | Force first (from the demo), then Stats, Inventory, Animation | 2-4 each | 0, 1, 2, 3 | each pack in parallel |
| 9 Second host (optional) | a 200-line Godot or server sample that runs the Force pack from JSON | 2 | 7, 8 | - |

Rough total: 30 to 40 agent-days, about half of it parallel.

Risks: the struct-interface rules change instruction order and move a bench number (mitigation: phase 3 lands behind the two hash tests and
the bench, one rule group at a time); Burst float contraction differs from the JIT and breaks hash parity on a new pack (mitigation: the
cross-host hash test per pack; pin `FloatMode.Strict` on that pack's jobs if it fails; today's fixtures already match on all hosts);
S-A fails (fallback: Unity glue wraps pack state in a generated `TlState<Force> : IComponentData`, with `RegisterGenericComponentType`, at
the cost of one wrapper name in Unity systems); S-B fails (fallback: a sync script per pack, like Core).

What stays the same for designers: everything. Timeline window, the Force track in the `+` menu, clip Inspector, Play, live edit in Play,
scrub preview, the Tl foldout, Project Settings > Tl. What stays the same for coders: the five Force files compile unchanged (`Tl` names
keep their namespace; `Tl.Core` is auto-referenced). What is new and optional: packs, `[TlState]`, `Tl > Export` JSON, `tl run`.

## 7. Questions for the owner

1. **Math type in packs.** Unity 7 ships `float3` inside the engine, so a pack using it cannot build outside Unity. Use Tl's own `float3`
   (same layout; Unity code converts at the edge with one call), or keep Unity's and accept that packs are Unity-only?
2. **Where packs live and who sees them.** In public tl under `packs/` (MIT, no tags or releases until you say), or in a private repo next
   to tl.unity?
3. **Loose game code.** Should data made from clip types in a project's `Assembly-CSharp` (no asmdef) also be runnable by `tl run`, or is
   "portable code lives in a pack" enough? The first needs pair keys without the assembly name, which re-pins the byte fixtures (state
   hashes stay).
