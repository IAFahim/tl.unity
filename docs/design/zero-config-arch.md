# Zero-config architecture plan (issue #22)

Companion to [zero-config-ux.md](zero-config-ux.md). The UX plan says what the designer and coder see. This plan says how the package makes
it true, which facts were checked on Unity 7000.0.0a16, and in which order to build it. No implementation before the owner has seen both.

Target: add the package git URL to a Unity 7 project and nothing else. A coder writes the five Force files of section 2, a designer adds a
Force track in Unity's Timeline window, drops the Timeline on a GameObject in a SubScene, presses Play, and the object moves. No `tlb`, no
.NET install, no JSON, no Library asset, no asmdef edit, no typed names, no generated file to commit.

## 1. Verified facts (spikes run for this plan)

All in throwaway projects outside the repo (`D:\GitHub\_work\probe-arch`), cleaned afterwards. Spike sources are kept in
`D:\GitHub\_work\probe-arch\spike-src` for whoever runs the phases.

| # | Question | Result on 7000.0.0a16 (Entities 6.7.0, Timeline 6.7.0) |
|---|---|---|
| F1 | Can a source generator emit the Unity `TrackAsset` / `PlayableAsset` classes for a `[TlClip]` struct? | **No.** The generated `ForceClipTrack`/`ForceClipAsset` exist in `TypeCache` and the Timeline menu, and a `.playable` saves, but it is written with `m_Script: {fileID: 0}` (no `MonoScript` for generated types). In the next Editor process both sub-assets load as `null`; the timeline is empty. |
| F2 | Package-owned `TlTrack` / `TlClip` classes holding the clip as `[SerializeReference]` to a generator-emitted plain `[Serializable]` mirror class | **Yes.** After a restart the sub-assets load as `TlClip, TimelineAsset, TlTrack`, the clip's data is `Tl.Generated.ForceClipData` with `Strength = 5`. Plain classes need no `MonoScript`. |
| F3 | A generator dll inside the runtime asmdef's folder (labelled `RoslynAnalyzer`), no marker asmdef | **Yes.** It ran on `Assembly-CSharp` (the Force files had no asmdef) and on every assembly referencing the runtime asmdef. This is how Entities ships its own generators. |
| F4 | `Baker<PlayableDirector>` reads `TimelineAsset` → tracks → clip assets on a16, and the coder's `ITlBaker<ForceClip>` adds a component, a buffer and a blob | **Yes.** Baked entity: `LocalToWorld, LocalTransform, ForceFalloff, Force, ForceLog, ...`; blob has 16 samples, first 1, last 0 (sampled from the default `AnimationCurve.Linear(0,1,1,0)`). |
| F5 | Incremental rebake: does a clip edit rebake the SubScene, and an unrelated asset not? | **Yes.** SubScene artifact hash unchanged after adding an unrelated asset; changed after setting the clip's `Strength` to 7 (`DependsOn(timeline, track, clip asset)`). |
| F6 | Can "Force" appear in the Timeline window's own `+` track menu with no per-type `TrackAsset` file? | **Yes.** The generator emits a never-saved menu stub `[DisplayName("Force")] sealed class ForceClipMenuTrack : TlMenuTrack`. Timeline lists it (menu entries: `Activation Track, ..., Signal Track, Force`). A package `TrackEditor` for `TlMenuTrack` replaces the stub in `OnCreate` with a real `TlTrack` named `Force` whose `Template` is `ForceClipData`. The saved `.playable` holds only `TimelineAsset, TlTrack`. Headless: Undo and the real window are spike S3. |
| F7 | Can `Tl.Gen.Tlb` (the packer behind `tlb`) compile inside Unity? | **Almost.** Compiled offline with Unity's own `csc` and the exact reference set of an a16 Editor assembly (`unity-4.8-api` + `BCLExtensions/netstandard2.1`, `System.Text.Json` and `System.Reflection.Emit` present): 13 errors at C# 14, all `System.Runtime.Intrinsics.Vector256` (SIMD fast path) and `System.Threading.Lock`; 80 at Unity's default C# 9. |
| F8 | Does a per-asmdef `csc.rsp` raise the language version on a16? | **Yes.** `-langversion:12` in `csc.rsp` was applied after Unity's `-langversion:9.0`; records then need the `IsExternalInit` polyfill, which `com.tl.unity/Core/Polyfills.cs` already has. The package already compiles Tl.Core this way (`-langversion:latest`). |
| F9 | Does Unity bundle .NET? | a16 ships `Editor/Data/DotNetSdk` (SDK 10.0.303, runtime 10.0.11). Not needed by this design; noted as the fallback for running `tlb` out of process with no user install. |

How the references do it (read only): BovineLabs Timeline bakes `PlayableDirector` directly with `Baker<PlayableDirector>`, every clip type is
a hand-written `DOTSClip : PlayableAsset` with ordinary serialized fields and a virtual `Bake(Entity, BakingContext)`, every track a
`DOTSTrack : TrackAsset`, and the baker calls `DependsOn` on timeline, tracks and clips; the user adds nothing but the director. vex-studio's
`com.bovinelabs.timeline.transform` follows the same pattern (one clip class file per clip type, `ExposedReference<T>` for scene objects).
`com.vex.preload` auto-injects SubScene hosts. We take the director-as-authoring and `DependsOn` pattern; we cannot take one-class-per-file
because the owner forbids it, which F2 and F6 solve.

## 2. The Force contract, final form

The five files of the UX plan, section 0.2, are the contract. Architecture agrees with all of it, including the UX corrections
(`[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]` and `using Tl;` in `ForceSystem.cs`). One more detail is fixed here, not in
the coder's code: `TlBaker` is a `ref struct` passed by `ref` (the coder cannot store it). The coder's files, with nothing added:

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

### 2.1 What each name is, against Tl today

| Contract name | Defined where | Maps to today |
|---|---|---|
| `TlClipAttribute(string menu)` | `Tl.Core` (sync from tl) | new; the generator keys on it |
| `TlKindAttribute` | `Tl.Core` | same meaning as `[TimelineKind]` (kept as an alias); `Accepts`, `Mode`, new `Inline` |
| `ITlTrack<TClip>` | `Tl.Core` | `ITrack<TlNoSettings, TClip>`; `TlNoSettings : IBlend<TClip>` keeps the first clip of a blend window |
| `TlFrame<TClip>` | `Tl.Core` | readonly view over `Frame<TlNoSettings, TClip>`: `Clip`, `Tick`, `Direction`, `Progress = WithinClip / (float)ClipLength` |
| `Feeds`, `Pulse` | `Tl.Core` | unchanged |
| `TlCurve` | `Tl.Core` | new unmanaged struct: 16 inline `float` samples over 0..1, Burst-safe linear `Evaluate`; 64 bytes inside the clip |
| `TlCurveBlob` | `Tl.Unity` runtime | new: `BlobArray<float> Samples` |
| `ITlBaker<TClip>`, `TlBaker` | `Tl.Unity` runtime (authoring part) | new; thin wrapper over Entities' `IBaker` (spike F4) |
| `TlSimulationGroup` | `Tl.Unity` runtime | rename of `TimelineSimulationGroup` (inside `FixedStepSimulationSystemGroup`) |
| `Apply(in float3, ref Force)` on a coder `IComponentData` | generator | new: today Apply targets columns declared in a `[TimelineSystem] ref partial struct` and generated as `System_FieldData` components. The generator now also accepts any unmanaged `IComponentData` as an Apply target and groups kinds into generated systems by the components they write. `[TimelineSystem]` stays for existing code and the benchmarks. |

### 2.2 Generated into the coder's assembly (never a file in `Assets/`)

Per `[TlClip]` struct, the generator (the existing `Tl.Gen.Unity`, moved next to the runtime asmdef, F3) emits:

```csharp
namespace Tl.Generated
{
    [System.Serializable]
    public sealed class ForceClip_TlData : global::Tl.Unity.TlClipData
    {
        public global::Unity.Mathematics.float3 Direction;
        public float Strength;
        public global::UnityEngine.AnimationCurve Falloff = global::Tl.Unity.TlCurveAuthoring.Default();
        public override string MenuName => "Force";
        public override global::Tl.Unity.TlClipType ClipType => global::Tl.Unity.TlClipType.Of<global::ForceClip, global::ForceTrack>();
        public override global::Tl.Unity.TlClipData CreateDefault() => new ForceClip_TlData();
        public override void Write(ref global::Tl.Unity.TlClipWriter writer, ref global::Tl.TlBaker baker)
        {
            var clip = new global::ForceClip { Direction = Direction, Strength = Strength, Falloff = global::Tl.Unity.TlCurveAuthoring.Sample(Falloff) };
            writer.Write(in clip);
            default(global::ForceBaking).Bake(ref baker, in clip);
        }
    }

    [global::System.ComponentModel.DisplayName("Force")]
    public sealed class ForceClip_TlMenu : global::Tl.Unity.TlMenuTrack
    {
        public override global::Tl.Unity.TlClipData CreateTemplate() => new ForceClip_TlData();
    }
}
```

plus what `Tl.Gen.Unity` already emits for kinds and feeders (kind entry buffers, the Burst kind tick and apply jobs, the registry row for
`ForceTrack.Evaluate`). Mirror field rules: unmanaged fields copy as is (`float3`, enums, `bool`, numbers are Unity-serializable);
`TlCurve` → `AnimationCurve`; `TlTimeline` → `TimelineAsset` field (drag and drop, baked to a reference, section 6); `TlBinding` (later) →
`ExposedReference<GameObject>`. A field type the mirror cannot draw is a generator error on the struct (`TLUNITY0201`), not a runtime surprise.

Why not a `TimelineAction` in the coder's assembly (UX 0.3 row 6): `Assembly-CSharp` cannot reference the Editor-only
`Unity.Timeline.Editor` assembly. The menu stub is a runtime type and needs nothing Editor-side in the coder's assembly (F6).

## 3. The whole baking path

```
Timeline window (.playable)              SubScene bake (Unity, incremental)                      Entity (SubScene)
 TlTrack "Force"  Template=ForceClip_TlData  TlDirectorBaker : Baker<PlayableDirector>              TlTimelineSet [slot 0: blob]
  TlClip  Data=ForceClip_TlData (mirror)      DependsOn timeline, Tl tracks, clip assets, settings   Playback [], TimelineRequest [Start slot 0]
 TlTimelineSettings marker (End, Channel,     per timeline: TlTimelinePacker (Editor, in process)    TimelinePlaying (disabled)
  Next)                                         per clip: mirror.Write -> unmanaged ForceClip bytes   ForceKind entry buffer (exact kinds)
PlayableDirector (Playable, Play On Awake,       -> ITlBaker<ForceClip>.Bake (component/buffer/blob)   Force (Apply target), ForceLog, ForceFalloff
 Initial Time) [+ TlAlsoPlays]                  -> Tl.Gen.Tlb typed bake -> TLB1 bytes -> blob       TlUnregistered (enabled)
                                               reached kinds -> kind buffers + Apply targets
Runtime: TlLibrarySystem creates the library singleton; TlTimelineRegisterSystem adds new blobs to it, writes ids into TlTimelineSet,
then the existing request, advance and kind systems run unchanged on the same LibraryView.
```

### 3.1 Authoring classes (package, `Tl.Unity.Timeline` runtime assembly; real files, so they have a `MonoScript`)

```csharp
using System;
using System.ComponentModel;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [HideInMenu]
    [DisplayName("Tl Track")]
    [TrackClipType(typeof(TlClip))]
    public sealed class TlTrack : TrackAsset
    {
        [SerializeReference] public TlClipData Template;

        protected override void OnCreateClip(TimelineClip clip)
        {
            ((TlClip)clip.asset).Data = Template?.CreateDefault();
            clip.displayName = Template?.MenuName;
        }
    }
}
```

```csharp
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    public sealed class TlClip : PlayableAsset, ITimelineClipAsset
    {
        [SerializeReference] public TlClipData Data;
        public ClipCaps clipCaps => ClipCaps.Blending;
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner) => Playable.Create(graph);
    }
}
```

```csharp
using System;

namespace Tl.Unity
{
    [Serializable]
    public abstract class TlClipData
    {
        public abstract string MenuName { get; }
        public abstract TlClipType ClipType { get; }
        public abstract TlClipData CreateDefault();
        public abstract void Write(ref TlClipWriter writer, ref TlBaker baker);
    }
}
```

```csharp
using UnityEngine.Timeline;

namespace Tl.Unity
{
    public abstract class TlMenuTrack : TrackAsset
    {
        public abstract TlClipData CreateTemplate();
    }
}
```

`TlTimelineSettings` is a package `Marker` at time 0 on the timeline's marker row (`[HideInMenu]`, drawn by a `MarkerEditor` as a small Tl
badge): `End` (Stop, Loop, Hold, Next), `Next` (`TimelineAsset`), `Channel`. It is a sub-asset of the `.playable`, so duplicate, rename and
copy keep it. The Timeline Inspector's Tl section (UX 3) edits it and creates it on first change. No marker means Stop, no channel.

Editor assembly `Tl.Unity.Timeline.Editor`: `TlMenuTrackEditor : TrackEditor` (replaces the stub, F6; wraps the swap in one Undo group),
`TlTrackEditor` (track colour and icon per clip type), a `PropertyDrawer`-free Inspector (the mirror is plain serialized fields, so Unity's
own Inspector, Undo and prefab logic apply).

### 3.2 The director baker (package, `Tl.Unity.Timeline.Editor`)

```csharp
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public sealed class TlDirectorBaker : Baker<PlayableDirector>
    {
        public override void Bake(PlayableDirector director)
        {
            var timelines = TlDirectorTimelines.Collect(this, director, GetComponent<TlAlsoPlays>());
            if (timelines.Count == 0)
                return;
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            var added = new HashSet<ComponentType>();
            var baker = new TlBaker(this, entity, added, director);
            var reached = new TlReachedKinds();
            var set = AddBuffer<TlTimelineSet>(entity);
            foreach (var timeline in timelines)
            {
                var blob = TlTimelinePacker.Pack(this, timeline, ref baker, ref reached);
                AddBlobAsset(ref blob, out _);
                set.Add(new TlTimelineSet { Timeline = blob });
            }
            AddBuffer<Playback>(entity);
            var requests = AddBuffer<TimelineRequest>(entity);
            if (director.playOnAwake)
                requests.Add(TimelineRequest.StartSlot(0, TlTicks.From(director.initialTime)));
            AddComponent<TimelinePlaying>(entity);
            SetComponentEnabled<TimelinePlaying>(entity, false);
            AddComponent<TlUnregistered>(entity);
            TlKindBaking.AddReached(this, entity, reached, added);
        }
    }
}
```

Order, fixed and documented:

1. `TlDirectorTimelines.Collect`: the director's `playableAsset`, then each `TlAlsoPlays` entry, then the closure of `Next` targets and of
   `TlTimeline` fields in clip data, each once, in that order. It calls `DependsOn` on every timeline, every `TlTrack`, every `TlClip` asset
   and every `TlTimelineSettings` marker it visits (F5). Timelines with no `TlTrack` are skipped with a foldout message.
2. `TlTimelinePacker.Pack` per timeline: tracks top to bottom with groups flattened, muted tracks skipped; clips by start tick, then authoring
   order. For each clip: ticks are `round(seconds * TickRate)` (off-grid clips reported on the clip); `clip.Data.Write(ref writer, ref baker)`
   produces the unmanaged clip value and, in the same call, runs the coder's `ITlBaker<T>.Bake` once for that clip on this entity.
3. The packer hands the typed timeline (track types, clip spans, raw clip bytes, end/next/channel from the settings marker, tick rate) to the
   synced `Tl.Gen.Tlb` typed bake (section 4.1). Out come the TLB1 bytes of that one timeline and its kind analysis (`KindLayout.Analyze`).
   Both go into `BlobAssetReference<TlTimelineBlob>`:
   `{ Hash128 Asset; Hash128 Content; uint TickRate; BlobArray<byte> Tlb; BlobArray<byte> Kinds; BlobArray<Hash128> NextAssets }`.
   `Asset` is the `.playable` GUID (stable identity), `Content` the hash of `Tlb + Kinds` (dedupe and change detection). The same
   timeline on 500 objects is one blob (`AddBlobAsset` deduplicates by hash).
4. `TlKindBaking.AddReached`: for each kind the set can reach (from the analyses, plus kinds of `[RequiresKinds]`-free game pulses declared
   with `[TlKind(GamePulses = n)]`), the kind's entry buffer and enable bit, and each `ref` parameter type of its `Apply` (here `Force`), unless
   already added by an `ITlBaker`. This is today's `TimelineKindSets` logic, fed from the analyses instead of `library.json`.

`TlBaker` (runtime assembly, so the coder's `ForceBaking` compiles in `Assembly-CSharp`):

```csharp
using System.Collections.Generic;
using Unity.Entities;

namespace Tl
{
    public ref struct TlBaker
    {
        readonly IBaker _baker;
        readonly Entity _entity;
        readonly HashSet<ComponentType> _added;
        readonly UnityEngine.Object _context;

        public TlBaker(IBaker baker, Entity entity, HashSet<ComponentType> added, UnityEngine.Object context)
        {
            _baker = baker;
            _entity = entity;
            _added = added;
            _context = context;
        }

        public void AddComponent<T>() where T : unmanaged, IComponentData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddComponent<T>(_entity);
        }

        public void AddBuffer<T>() where T : unmanaged, IBufferElementData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddBuffer<T>(_entity);
        }

        public void AddBlob<T>(T component) where T : unmanaged, IComponentData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddComponent(_entity, component);
            else
                TlBakeReport.SecondValueIgnored<T>(_context);
        }

        public BlobAssetReference<TlCurveBlob> CreateCurve(in TlCurve curve)
        {
            var blob = TlCurveBlob.Create(curve);
            _baker.AddBlobAsset(ref blob, out _);
            return blob;
        }
    }
}
```

Semantics: `AddComponent`/`AddBuffer` idempotent per entity; `AddBlob` first clip wins, later clips with a different value get the UX
message (UX 0.3 row 4); an exception in a coder's `Bake` skips that clip and reports clip, time, timeline and object.

### 3.3 What lands on the Force demo entity

`LocalTransform, LocalToWorld` (Unity), `TlTimelineSet [1]` (blob), `Playback []`, `TimelineRequest [Start slot 0]`, `TimelinePlaying`
(disabled), `TlUnregistered` (enabled), `ForceKind` entry buffer and enable bit, `Force`, `ForceLog []`, `ForceFalloff` (blob), plus Entities
Graphics components for the visible cube. No per-clip or per-track entity, no library on the entity.

### 3.4 Runtime: finding the library with no asset

- `TlLibrarySystem` (`InitializationSystemGroup`, `ISystem`) creates one library entity in `OnCreate`: today's `TimelineLibrary`
  (`LibraryView`, `KindIndex`, `Generation`) plus `TlLibraryIndex` (unmanaged: `NativeHashMap<Hash128, ushort>` asset → id). The managed
  `TimelineWorld` that owns the bytes lives in a class component on that entity, created and disposed by the system. No statics, so Enter
  Play Mode without domain reload and several worlds are safe. This replaces `TimelineLibraryBootstrap`, its static dictionary, its file
  path and `PollReload`.
- `TlTimelineRegisterSystem` (same group, after it). Steady state: a Burst-compiled `IsEmpty` check of the `TlUnregistered` query, nothing
  else. When entities with the tag appear (SubScene loaded, live rebake, instantiated prefab), the slow path (not Burst, main thread, once per
  batch) collects their blobs, adds each `Asset` not known yet (or whose `Content` changed) to the library in deterministic order (sorted by
  `Asset`), reassembles the library bytes (section 4.2), calls `TimelineWorld.Reload`, bumps `Generation`, writes the id into every
  `TlTimelineSet` element and resolves `StartSlot` requests to `TimelineRequest.Start(id)`, then disables the tag. A changed timeline keeps
  its id, so running `Playback` entries keep their tick (clamped to the new duration).
- World creation is Entities' default bootstrap. Player builds bake the SubScene at build time; the blobs travel in the entity scene; the
  register path is plain C# with no `Reflection.Emit`, so it runs on IL2CPP and CoreCLR players (both still proven by spike S5).

## 4. Design decisions

Each: decision, reason, cheaper alternative rejected.

### 4.1 Q1 Where library data comes from, and what replaces `tlb library` / `tlb kinds`

- **Decision:** Timeline assets are the only source. The director baker packs each timeline in process into a TLB1 blob in the SubScene.
  `Tl.Gen.Tlb` (the code behind `tlb`) is source-synced into the package Editor assembly `Tl.Unity.Bake` by a new `eng/sync-tl-bake.sh`,
  exactly like `eng/sync-tl-core.sh` syncs Tl.Core today (`csc.rsp -langversion:latest`, F8). Two small changes in tl: guard the
  `Vector256` fast path and `System.Threading.Lock` behind `#if NET` (the scalar parser already exists, F7), and add a typed entry point
  `TimelineBaker.BakeTyped(TypedTimeline)` that takes track/clip `Type`s and raw clip bytes, sharing the writer that the JSON path ends in.
  Type resolution uses the types already loaded in the Editor domain (no `Assembly.LoadFrom`, no staged dlls).
- **Reason:** one packer implementation for Unity, .NET and plain C# hosts, so `game.tlib` byte identity and the pinned hashes stay
  testable; no process spawn (live edit stays fast); nothing to install.
- **JSON:** not in the user path at all. `Tl > Export Library` (Editor, advanced) writes `library.json`, per-timeline json and `game.tlib`
  for CI, the plain C# host and diffs.
- **Rejected:** a new Unity-only blob format written by a `TlTimelineCompiler` (UX B4 step 6): a second library format and a second runtime
  reader to keep in step, and the hash tests would no longer cover Unity. Running a prebuilt `tlb` with Unity's bundled `dotnet` (F9):
  works with no install but spawns a process per bake, needs staged assemblies, and depends on an undocumented Editor folder. In-memory JSON
  into the existing `BakeJson`: cheap to start with but the JSON path cannot write nested structs or fixed buffers (`TlCurve`), so it is only
  the fallback if `BakeTyped` slips.

### 4.2 Q2 Zero-config bootstrap

- **Decision:** per-timeline blobs on the entities, one runtime library assembled from the blobs that are loaded (3.4). The library
  assembler (`TlLibraryAssembler`, runtime, plain C#) does what `LibraryPack.Emit` does in `tlb`: header, names, timeline table, per-timeline
  TLB bytes, kind section from the per-timeline analyses plus the kind catalog the generator registers. Assembling the KindGame fixture's
  timelines in `tlb`'s order must give `tlb`'s `game.tlib` byte for byte (spike S2, acceptance in phase 3).
- **Reason:** follows Unity's own model (data in the SubScene, exact `DependsOn`, streaming and several SubScenes for free, dedupe across
  SubScenes by `Asset`/`Content`), no hidden asset.
- **Rejected:** one project-wide library built by a `ScriptedImporter` on a package-owned file and embedded in every SubScene: unchanged
  runtime, but every clip edit rebakes every SubScene, the importer must track "all `.playable` files" through a custom dependency, and it
  is an asset after all. Per-SubScene libraries: id spaces collide when two SubScenes load.

### 4.3 Q3 Clip types become Timeline-window tracks

- **Decision:** F2 + F6. Package-owned `TlTrack`/`TlClip`, generated `[SerializeReference]` mirror (typed Inspector fields, curve editor,
  object fields), generated menu stub swapped by a package `TrackEditor`. Renames: the generator emits `[UnityEngine.Scripting.APIUpdating.MovedFrom]`
  only when the coder adds it; a missing type keeps its serialized data (Unity keeps unresolved managed references) and the clip shows
  "Clip type ForceClip not found (compile error or renamed)".
- **Reason:** proven on a16; data survives compile errors because the Unity object types never disappear.
- **Rejected:** generated `TrackAsset`/`PlayableAsset` (F1, data loss after restart). One hand-written class file per type (BovineLabs):
  works, but the owner forbids extra coder files. A single "Tl Track" with a type dropdown: works, but "Force" would not be in the track menu.

### 4.4 Q4 Generator wiring and kind capacities

- **Decision:** move `Tl.Gen.Unity.dll` and `Tl.Gen.Plain.dll` into the `Tl.Unity` runtime asmdef folder (F3); delete
  `Tl.Unity.Generate*.asmdef`. The generator exits in microseconds when the compilation has no `[TlClip]`, `[TlKind]`, `[TimelineKind]`,
  `ITrack` or `[TimelineSystem]`; it emits ECS code when the compilation references `Unity.Entities`, plain code otherwise. Mark `Tl.Unity`,
  `Tl.Core`, `Tl.Unity.Timeline` `autoReferenced: true`.
- **Capacities:** no `Kinds.g.cs`. The generator computes each kind's `[InternalBufferCapacity]` at compile time from the kind's entry size
  and an inline budget (default 128 bytes, the current `maxInlineBytes` default), overridable with `[TlKind(Inline = n)]`. Worst cases are
  computed at library assembly (data, `KindLayout.Capacities` arithmetic) and only reported: the Kind Report and the director foldout say
  "Damage: worst 14, inline 11, spills to heap on 3 objects". A spill is correct, only slower. So editing a timeline never compiles code.
- **Rejected:** keep `tlb kinds` writing C#: the recompile loop the owner hates. Capacity 0 everywhere: heap access for every kind, a
  measured regression.

### 4.5 Q5 Start, stop, pause from code and Inspector without names

- **Decision:** `TlTimeline` (authoring-side field type in a coder's own `[TlClip]` or MonoBehaviour: a `TimelineAsset` object field) bakes to
  `TlTimelineRef { Hash128 Asset }` via `TlBaking.GetTimeline(this, asset)` (adds the timeline to the entity's `TlTimelineSet` if missing
  and `DependsOn`). Burst API on the entity's request buffer: `TlRequests.Start(ref requests, in TlTimelineRef)`, `Stop`, `Pause`, `Resume`,
  resolved to ids by `TlLibraryIndex` in `TimelineRequestSystem`. New `RequestOp.Pause`/`Resume` set and clear `PlaybackFlags.Paused`, which
  Core's advance and `KindStore` already honour. Designer control: the UX `Tl Trigger` uses the same refs. No generated id constants.
- **Rejected:** generated `Timelines.Dash` constants (names, recompiles on asset changes).

### 4.6 Q6 Live editing

- **Decision:** Unity live baking. Clip edit → `DependsOn` → incremental rebake of the objects whose sets reach that timeline → new blob
  patched onto the runtime entity with `TlUnregistered` enabled → register system swaps the timeline under the same id, Playback keeps its
  tick. A layout change of a clip struct is a script change: Unity recompiles; stale blobs whose `ClipType` digest no longer matches are
  rejected with a message until the rebake that follows the compile.
- **Rejected:** today's file-stamp `PollReload` (needs a file and a library path).

### 4.7 Q7 Performance

The tick path does not change: same `Playback` buffer, same `LibraryView` over TLB1 bytes, same kernels and kind jobs. Parts that could move
the numbers, and the measurement for each (owner numbers: A 7.3-8.5, B 1.8-2.0, B0 about 0.6 ns/entity, 0 B warm, hashes
`13943614770702343869` and `6607956939547305625`):

| Change | Risk | Measured by |
|---|---|---|
| Library assembled from blobs | different bytes → different hash | KindGame fixture assembled in `tlb` order equals `tlb`'s `game.tlib` byte for byte; both hash tests unchanged |
| Computed `InternalBufferCapacity` instead of `Kinds.g.cs` | spills or bigger chunks | fixture kinds pinned with `[TlKind(Inline = 11/2/2/3)]`, same as today's file; K benchmarks A/B/B0 before and after |
| Apply into coder `IComponentData` | a slower generated access path | new bench variant of B with `Force`-style columns; must be within B's band |
| `TlTimelineRegisterSystem` every frame | per-frame cost, GC | Burst `IsEmpty` only; warm frame 0 B in the existing GC test; profile marker under 1 µs |
| `TlCurve` in clips | bigger clip payload | only clips that use it; reported in the Kind Report bytes |
| Generator on all assemblies | compile time | Editor compile time of an empty project with and without the package, under 5% |

### 4.8 Q8 Migration

| Today | Fate |
|---|---|
| `tlb`, `Tl.Bake`, JSON libraries in the tl repo | kept for .NET and plain C# hosts, CI and the hash oracle; not used by Unity |
| `TimelineLibraryAsset`, `TimelineBridgeAsset`, `TimelineBridgeBake`, `TimelineJson`, `KindCapacityBake`, `TimelinePlayerAuthoring` (Start With / May Start strings), `TimelineDirectorAuthoring`, `TimelineLibraryBootstrap` statics and `game.bytes` loading | deleted in phase 7, after the new path passes the same tests |
| `TlTrack<T>`, `TlClip<T>`, `TlMarker<T>`, `TlField` text storage | replaced by `TlTrack`/`TlClip` + mirrors; a one-shot `Tl > Upgrade Timeline Assets` converts old sub-assets in the repo's own samples (the package is preview, no user migration promised) |
| `Tl.Unity.Generate` marker asmdefs | deleted (F3) |
| Plain C# host | kept; loads `game.tlib` exported by `Tl > Export Library` or built by `tlb` |
| Tests | hash, rule, Burst, scene tests kept; library inputs come from the assembler, with `tlb`-built fixtures as oracle; bridge tests rewritten against `TlTrack` |
| `eng/tl-init.sh`, one-liners, installers in tl | deleted; install is the git URL |
| Samples | `EcsDemo` replaced by the Force demo with a camera (UX section 4); IL2CPP samples kept |

### 4.9 Answers to the UX planner's open questions (zero-config-ux.md section 9)

1. Yes: inside the SubScene bake, in process, microseconds to milliseconds per timeline; no importer, no external process.
2. Blobs on the director entities; dedupe by `Asset`/`Content` across objects and SubScenes; one library singleton built at runtime.
3. Generated `TrackAsset`s fail on a16 (F1). Mirror route passes (F2); `Force` in the `+` menu via the stub swap (F6). Coder writes zero
   Unity classes.
4. Yes: capacities are compile-time defaults or `Inline`, worst cases are data (4.4). Editing a timeline never compiles.
5. A `TlTimelineSettings` marker inside the `.playable` (survives duplicate/rename). Not the director's Wrap Mode: one timeline asset must
   mean one library entry, whoever plays it. The director foldout may show a hint when Wrap Mode disagrees.
6. Yes: every `ref` parameter of a reached kind's `Apply` is added (4.1 step 4). "Starting values" overrides: a later phase, as a
   `TlStartValues` component edited in the foldout.
7. Yes: `RequestOp.Pause`/`Resume` (4.5).
8. Yes: same id, tick kept (4.6); latency target under one second, spike S4.
9. Later phase: `TlBinding` clip field → `ExposedReference<GameObject>` in the mirror → `TlBindings` entity buffer resolved by the baker.
10. Depend on `com.unity.entities.graphics` so SubScene objects render; the Force demo uses the URP default of a new Unity 7 project. Verified
    in spike S5 before phase 4 ships.
11. `TlProjectSettings` (`ScriptableSingleton` stored in `ProjectSettings/TlSettings.asset`, default 60 ticks per second), baked into every
    blob; the register system rejects mixed rates.
12. Out of the first phases. Later: a `TlPreviewSystem` in an Editor-world group driven by the Timeline window time, restoring baked values
    on focus loss.
13. Confirmed by F2, F6. `[MovedFrom]` for renames is in 4.3; Undo of the swap is spike S3.
14. `TlCurve` 16 samples (64 B), linear between samples, error under 1% of range for ease curves; `Progress` = `WithinClip / ClipLength`;
    `ITlBaker` once per clip in the 3.2 order; `AddBlob` first wins.
15. Confirmed by F3 for `Assembly-CSharp`; `autoReferenced` assemblies make the five files need no asmdef. A coder who uses their own asmdef
    adds `Tl.Unity` and `Unity.Timeline` references (the generated mirror and stub need both).

## 5. Phases

Each phase ships alone, keeps all existing tests green, and keeps the hashes and benchmark bands. The agent doing a phase proves it with the
listed commands and reads back the result, not a claim.

| Phase | Delivers | Owner-runnable acceptance | Agent must prove |
|---|---|---|---|
| 0 Spikes | S2 to S6 below (S1 done in this plan) | read each spike's result table on #22 | each spike's yes/no with logs |
| 1 In-process packer | `eng/sync-tl-bake.sh`, `Tl.Unity.Bake` Editor assembly, tl `#if NET` guards, `BakeTyped` | `eng/test-com-tl-unity.sh` green; Editor test `PacksTheKindGameFixtureLikeTlb` | byte-equal TLB per fixture timeline vs `tlb`; no child process during the test |
| 2 Attributes and generator | `TlClip`, `TlKind`, `ITlTrack`, `TlFrame`, `TlCurve`, `ITlBaker`, `TlBaker`, `TlSimulationGroup`; generator in the runtime folder; Apply into `IComponentData`; no `Kinds.g.cs` | fresh project + git URL + the five Force files: 0 `error CS` | generated types present (`Type.GetType`), K benches A/B/B0 in band, hashes unchanged |
| 3 Library from blobs | `TlTimelineBlob`, `TlLibraryAssembler`, `TlLibrarySystem`, `TlTimelineRegisterSystem`, no statics | existing PlayMode and hash tests green | assembler output byte-equal to `tlb`'s `game.tlib`; 0 B warm frame |
| 4 Timeline authoring and baker | `TlTrack`, `TlClip`, mirrors, menu stub swap, settings marker, `TlDirectorBaker`, kind auto-add, Entities Graphics | Test A of the UX plan: Force track in the `+` menu, clip Inspector, Play, the cube moves | entity query: `Force`, `ForceLog`, `ForceFalloff`, kind buffer present; position changes; editor restart keeps the clip |
| 5 Live edit | register swap with tick kept, layout-change rejection | change Strength in Play: the push changes within a second, no exit from Play | SubScene artifact hash test (F5) in CI; Playback tick before/after swap |
| 6 Control | `TlTimeline` field, `TlTimelineRef`, `TlRequests` with Pause/Resume, Tl Trigger | Inspector trigger starts Dash on click, pause button pauses | Burst request API test; rewind hash test with pause in the middle |
| 7 Delete and document | 4.8 deletions, Force demo sample with camera, docs rewritten | grep: no `TimelineLibraryAsset`, `tlb` or `Kinds.g.cs` in package and docs; demo plays from Package Manager import | `eng/check-ecs-demo-sample.py` updated, green |
| 8 Players | IL2CPP and CoreCLR player builds of the Force demo and the KindsIL2CPP sample | player logs `hash ok` | both hashes in the player logs equal the .NET hashes |

## 6. Spikes (1 day each, before the phase that needs them)

| Spike | Question | Done when |
|---|---|---|
| S1 (done) | generated SO, mirror route, generator auto-apply, Timeline in a Baker, blob, rebake on edit, `+` menu | section 1, F1 to F6 |
| **S2 (first)** | per-timeline TLB + kind analysis assembled at runtime equals `tlb`'s `game.tlib` for KindGame; which runtime code reads kind-section capacities | byte compare passes, or the exact differing field is named with a fix |
| S3 | real Timeline window, human driven: stub swap with Undo/Redo, duplicate `.playable`, copy/paste track, compile error keeps clip data | screenshots + checklist on #22 |
| S4 | live baking in Play mode on a16: patched `TlTimelineSet` reaches the runtime world, swap keeps tick, latency | measured latency under 1 s |
| S5 | player builds (IL2CPP, CoreCLR) load entity scenes with `BlobArray<byte>` TLB; Entities Graphics + URP shows the cube | player log + screenshot |
| S6 | generator Apply into a coder `IComponentData`: Burst code and bench B variant | B variant within B band |

Risks: Timeline's `OnCreate` swap breaks with a Timeline update (mitigation: fall back to the single `Tl Track` + type dropdown, data format
unchanged); SerializeReference + missing types behaviour on future editors (S3 checks it); `Tl.Gen.Tlb` drifts from Unity's API surface
(the sync script compiles it with Unity's references in CI, as F7 did); several SubScenes with different tick rates (rejected with a message).

## 7. Phase 2 results and S6

Built on 7000.0.0a16, Entities 6.7.0. Proven by `eng/test-com-tl-unity.sh` (EditMode 65/65, PlayMode 49/49, 0 `error CS`, 0 warnings)
and a fresh project (package from a local path, no testables, no marker asmdef, no Library asset) with the five Force files in `Assets/`:
0 `error CS`, 0 warnings, and `Type.GetType` finds `Tl.Generated.ForceClip_TlData`, `TlUnityGenerated.ForceKindEntry`,
`Tl.Generated.TlAuto_Assembly_CSharp_Jobs` and the apply job in `Assembly-CSharp`.

| Name | Where | Notes |
|---|---|---|
| `TlClipAttribute(string menu)`, `TlKindAttribute` (`Accepts`, `Mode`, `Order`, `Group`, `FoldsPulses`, `Inline`) | `Tl.Unity` runtime, namespace `Tl` | `[TimelineKind]` still works |
| `ITlTrack<TClip>` | same | `: ITrack<TlNoSettings<TClip>, TClip>`; `TlNoSettings<TClip> : IBlend<TClip>` keeps the first clip |
| `TlFrame<TClip>` | same | built from `Frame<TlNoSettings<TClip>, TClip>`: `Clip`, `Tick`, `ClipLength`, `WithinClip`, `Direction`, `Progress` |
| `TlCurve` | same | 16 inline `float` samples (64 B), linear `Evaluate(progress)`, Burst-safe |
| `TlCurveBlob` | same assembly, **global namespace** | `ForceData.cs` has no `using Tl;`, so the blob type must resolve without it |
| `ITlBaker<TClip>`, `TlBaker` (ref struct), `TlBakeReport` | same, namespace `Tl` | `Tl.Unity` now references `Unity.Entities.Hybrid` for `IBaker` |
| `TlSimulationGroup` | same, namespace `Tl` | in `FixedStepSimulationSystemGroup`; `TimelineSimulationGroup` now runs inside it, so `OrderLast` systems see every kind applied |
| `TlClipData`, `TlCurveAuthoring`, `TlClipBytes` | `Tl.Unity` runtime, namespace `Tl.Unity` | base of the generated mirror; phase 4 adds `TlTrack`/`TlClip` on top |
| `TlKindSystem<TJobs>`, `ITlKindJobs` | same | generic `ISystem` in `TlSimulationGroup` after `TimelineSimulationGroup`; each assembly registers its instance |

Generator: `Tl.Gen.Unity.dll` moved to `com.tl.unity/Runtime/` (F3). It skips `Tl.Unity`, `Tl.Core`, assemblies with no Tl marker text and
assemblies that carry checked-in `TlUnity.*.g.cs` output (the KindGame fixture). `Tl.Unity.Generate*.asmdef` stay as empty markers until
phase 7. The type-registration ILPostProcessor now also processes every assembly that references `Tl.Unity`. In one analysis pass the
generator rewrites `[TlKind]` to `[TimelineKind]` and `TlFrame<T>` to `Frame<TlNoSettings<T>, T>`, then gives every kind whose `Apply`
writes only unmanaged `IComponentData` types (and no `[TimelineSystem]` column) to a generated system `Tl.Generated.TlAuto_<assembly>`. Its
columns are the coder's own components (no `_Data` wrapper). Per `[TlClip]` struct it emits `Tl.Generated.<Clip>_TlData : TlClipData`
(`[Serializable]`, `TlCurve` fields as `AnimationCurve`, `ToClip`, `WriteClip`, `Bake` calling every `ITlBaker<Clip>`). Inline capacity:
`[TlKind(Inline = n)]`, else a legacy `KindCapacity` constant, else `128 / entry bytes`. There is no `Kinds.g.cs` and no warning.

**S6 (passed).** `Tests/Generated/Direct`: a kind applying straight into `Stride : IComponentData` through the generated system, packed with
tlb, runs the same B shape as the K bench. Same process, Burst on, 100000 entities:

| Row | K bench | S6 direct |
|---|---|---|
| A all playing | 7.4 ns, 0 B | 6.8 ns, 0 B |
| B tenth playing | 1.9 ns | 1.1 ns |
| B0 none playing | 0.6 ns | 0.2 ns |

The direct rows do one kind and one apply system where the K bench does four kinds in two systems, so they are lower, not slower. Both pinned
hashes are unchanged (`KindHashTests` green). Open: tlb and `Tl.Gen.Tlb` read `[TimelineKind]` only, so the S6 fixture uses
`[TimelineKind]`; the phase 1 packer must accept `TlKind` and `ITlTrack` feeders before phase 4.

## 8. Phase 1 results and S2

Built on 7000.0.0a16. tl changes on IAFahim/tl main: e016d753 (`TimelineBaker.BakeTyped(TypedTimeline)`, AVX2 paths and
`System.Threading.Lock` behind `#if NET`, .NET-only BCL calls through `NetCompat`, `Tl.Core` grants internals to `Tl.Unity.Bake`) and
f9ce66cf (`[TlKind]` read like `[TimelineKind]`, its `Inline` sets the kind section capacity; feeders on `ITlTrack<TClip>` consumers are
found through the inherited `ITrack<TlNoSettings<TClip>, TClip>`). `dotnet test tl.slnx -c Release`: 893/893.

- `eng/sync-tl-bake.sh [--check]` copies `tools/Tl.Gen.Tlb` from the `external/tl-kinds` pin into `com.tl.unity/Editor/Bake/Tlb`
  (without the CLI-only `BakeCacheKey.cs`, `TlbInspection.cs`, `TlbReport.cs`) and writes `Tools~/tl-bake.stamp` (pin + source hash).
  `--check` builds nothing; `eng/test-com-tl-unity.sh` runs it next to the Core and generator checks.
- `Tl.Unity.Bake` (Editor only, `noEngineReferences`, `-langversion:latest`): `TlBake.BakeTyped(TypedTimeline, LibraryManifest?)` is the
  entry point for `TlDirectorBaker`; `TlBake.Manifest`, `TlBake.Catalog(assemblies)` and `TlBake.Analyze(name, tlb, catalog)` give the
  per-timeline kind analysis. The track `Type` of an `ITlTrack<TClip>` clip is `TlNoSettings<TClip>`.
- F7 understated the work: with the missing `System.Runtime.Intrinsics` namespace fixed, Unity's compiler reported 24 errors from .NET-only calls
  (`Utf8.IsValid`, `Rune`, `Stopwatch.GetElapsedTime`, `SHA256.HashData`, `Convert.ToHexString`, `CreateDelegate<T>`, `BitOperations`,
  `OperatingSystem`). Unity a16 does not define `NET`.
- `PacksTheKindGameFixtureLikeTlb`: the nine fixture timelines packed in process (20 rounds, `PATH` and `TL_TLB` cleared, a 1 ms child
  process poll running) equal the per-timeline TLB bytes inside `tlb`'s `game.tlib`, with no child process. 0.06 s for the whole test.
- Full run: EditMode and PlayMode green, 0 `error CS`, 0 warnings; both pinned hashes unchanged; K bench A 7.8, B 1.9, B0 0.6 ns/entity,
  0 B warm.

**S2 (passed with one condition).**

| Question | Result |
|---|---|
| Per-timeline TLB + `KindTimeline.Analyze`, assembled in process in `tlb`'s order (`LibraryPack.Emit` + `KindLayout.Section`), equals `game.tlib`? | **Yes**, byte for byte, with the fixture's `library.json` kind settings (`Damage: gamePulses 8, maxInlineBytes 132`) and 2 channels |
| Same without those settings? | Differs in exactly one field: the `Damage` kind row `Capacity` (kind table row offset 16). Nothing else, digest aside |
| Which runtime code reads kind-section capacities? | Only `Tl.Core` `KindStore` (`KindTable.Create(..., row.Capacity / row.GroupCapacity, ...)`, the managed `TimelineWorld` kind tables of the plain C# host). The ECS path never reads them; it uses `[InternalBufferCapacity]` from the generator. `KindSection` only range-checks them |
| What else a per-timeline blob cannot know | TLB header `Next` (offset 52), `Channel` (56) and any `TimelineId` clip field hold library-wide ids from the manifest |

Fix for phase 3: the assembler takes `gamePulses` and the inline budget from the kind catalog (`[TlKind(Inline = n)]` is read since
f9ce66cf; `GamePulses` still needs a `TlKind` property), with `channelCount` and `stackDepth` from project settings, so no `library.json`
is needed; and it patches `Next`, `Channel` and `TimelineId` fields after assigning ids (or bakes against a manifest built from the sorted
`Asset` set, as the test does with names).

## 9. Phase 3 results

Built on 7000.0.0a16. `eng/test-com-tl-unity.sh`: EditMode 68/68, PlayMode 55/55 (6 new), 0 `error CS`, 0 warnings. K bench (same
process, Burst on, 100000 entities): A 7.8, B 1.8, B0 0.6 ns/entity, 0 B. Both pinned hashes unchanged on the legacy route and reproduced
on the blob route.

| Name | Where | What |
|---|---|---|
| `TlTimelineBlob` | `Tl.Unity`, `Runtime/Library` | `Hash128 Asset, Content; uint TickRate; BlobArray<byte> Name, Tlb, Kinds; BlobArray<Hash128> NextAssets` (`Unity.Entities.Hash128`). `Name` was added: `TimelineWorld` needs ordinal-sorted names and tlb byte identity needs the real names |
| `TlTimelineBlobs.Create(asset, tickRate, name, tlb, kinds, nextAssets)` | same | builds the blob; `Content` is the first 16 B of SHA-256 over rate, name, tlb, kinds, next |
| `TlTimelineSet` (buffer: `Blob`, `Id`), `TlUnregistered` (enableable tag) | same | what the director baker puts on the entity |
| `TlLibraryAssembler.Assemble(parts, tickRate, settings)` | same | `LibraryPack.Emit` + `KindLayout.Capacities/Section` from the per-timeline kind payloads; patches TLB `Next` (offset 52) |
| `TlLibrary` | same | managed registry behind a `GCHandle` in `TlLibraryHandle`: assets → ids, dedupe, swap, owns `TimelineWorld` + `KindIndex` |
| `TlLibrarySystem`, `TlTimelineRegisterSystem` | same, `InitializationSystemGroup` | create the library entity on the first pending blob; register, publish `TimelineLibrary` + `TimelineStep` + `ClosedPlayback`, write ids, disable the tag |
| `TlLibraryIndex` | same | `NativeHashMap<Hash128, ushort>` asset → id for phase 6 refs |
| `TlBake.Catalog(assemblies, overrides)`, `TlBake.KindBytes(name, tlb, catalog)`, `TlKindKnobs` | `Tl.Unity.Bake` (Editor) | per-timeline kind payload: catalog section of one timeline + `MaxSpans`, `MaxPulses`, `Inline`, `GamePulses` per kind |
| `TlKindAttribute.GamePulses` | `Tl.Unity` | read by `TlBake.Catalog` next to `Inline` |

Rules the code keeps:

- Ids: a batch is sorted by (name, `Asset`) and appended; a known `Asset` keeps its id when its `Content` changes; equal `Asset` and
  `Content` is skipped (no reassembly). A name that would break the ordinal order gets a `U+FF5E` prefix in the library only, so ids already
  handed out never move when a later SubScene adds a timeline.
- `Next`: a blob is baked against a local manifest (`TlBake.Manifest(new[] { nextName }, channels, rate)`, id k = `NextAssets[k-1]`); the
  assembler writes the library id, or 0 until the target is loaded, and every reassembly re-resolves it.
- Tick rates: the first registered rate wins; a different one is rejected with "baked at 120 ticks per second but the library runs at 60".
- Kinds: every blob must carry the same catalog (rows, feeders, strings, knobs) or the batch is rejected ("baked against different kinds";
  rebake after the compile). Capacities use tlb's defaults (stack depth 1, 64 B inline budget); the channel count is the largest channel in
  the blobs. Only the plain host's `KindStore` reads them.
- Legacy: `TimelineLibrarySystem` stops when a `TlLibraryHandle` exists; the register system refuses to publish next to a legacy
  `TimelineLibrary`. No statics, no asset, no file path on the new route; the world's systems free everything in `OnDestroy`.
- Per frame: both systems `RequireForUpdate` the pending query and return on `IsEmpty`. `RequireForUpdate` alone ran the register system
  every frame after the tag was disabled (264 B/frame); with the `IsEmpty` check the warm frame is 0 B.

| Acceptance | Result |
|---|---|
| Blobs assembled (shuffled input) equal tlb's `game.tlib` byte for byte | pass, `AssemblesTheKindGameLibraryLikeTlbFromBlobs` |
| World with no asset and no static, library only from `TlTimelineSet` blobs on 3 entities (overlapping sets) | `SixHundredStep` hash 13943614770702343869 and events hash 6607956939547305625, same as the legacy route |
| Known blobs skipped, later timeline keeps earlier ids, changed timeline keeps its id, mixed rate rejected | pass |
| Warm frame (library + register systems + tick, 64 frames) | 0 B |

Open:
- `TimelineId` fields inside clips are not patched (the fixture has none); phase 6 replaces them with `TlTimelineRef`.
- The KindGame fixture keeps `[TimelineKind]` and `library.json` because `EditingATimelineWithoutChangingACapacityRewritesNothing` drives
  the old tlb. The blob test passes Damage `Inline = 11, GamePulses = 8` as `TlKindKnobs`, the values `[TlKind]` would carry. tlb itself
  reads `[TlKind(Inline)]` but not `GamePulses` yet; on the Unity route the runtime assembler is the only reader.
- `Tests/Library` is a PlayMode assembly with a `UNITY_EDITOR` constraint so it can reference the Editor-only `Tl.Unity.Bake`.

## 10. Phase 4 results

Built on 7000.0.0a16 (Entities and Entities Graphics 7.0.0, Timeline 6.7.0, URP 17.7.0, all built in). `eng/test-com-tl-unity.sh`:
EditMode 76/76 (8 new in `Tests/Timeline`), PlayMode 55/55, 0 `error CS`, 0 warnings, both pinned hashes unchanged. K bench (two runs, same
process, Burst on, 100000 entities): A 8.1 / 7.9, B 1.8 / 1.9, B0 0.6 / 0.7 ns/entity, 0 B.

| Name | Where | What |
|---|---|---|
| `TlTrack`, `TlClip`, `TlMenuTrack`, `TlMenu`, `TlTimelineSettings` + `TlEnd` | `Authoring/Timeline`, runtime asmdef `Tl.Unity.Tracks` (auto-referenced) | as 3.1; `Tl.Unity.Timeline` stays the legacy bridge's name until phase 7 |
| `TlPlayOnAwake` (enableable), `TlPlayOnAwakeSystem` | same | Burst `ISystem` after `TlTimelineRegisterSystem`: once the tag is off, adds `TimelineRequest.Start(0, id, at)` and disables itself (the register system does not resolve slots) |
| `<Clip>_TlMenu : TlMenuTrack` | generator (`UnityMirrorEmitter`) | `[DisplayName(menu)]` stub, emitted only when the compilation sees `Tl.Unity.TlMenuTrack` |
| `TlMenuTrackEditor`, `TlTrackSwap.Replace` | `Editor/Timeline`, `Tl.Unity.Tracks.Editor` | delete stub + create `TlTrack` named after the menu, collapsed into one Undo group |
| `TlClipEditor` | same | draws the mirror's own serialized fields (`PropertyField`, Unity Undo); no names, no type box |
| `TlDirectorBaker`, `TlDirectorTimelines`, `TlTimelinePacker`, `TlKindBaking` | same | 3.2 order: Playable then the `Next` closure, `DependsOn` every timeline, marker track, settings marker, Tl track, parent group and clip asset; muted tracks skipped; ticks `round(s * rate)`; coder `Bake` once per clip (an exception skips the clip with the UX message); `TlBake.BakeTyped` + `KindBytes` into one blob; kind entry/active/enabled + every column of the kind's system (`Force`) |
| `TlProjectSettings` + `Project Settings > Tl` | same | `ProjectSettings/TlSettings.asset`, 60 ticks/s default, baked into every blob; `Assets > Create > Tl > Timeline` presets the frame rate |

Decisions made while building:
- Kind catalog = the assemblies that register kinds with Unity (`KindRegistry` sources), not every loaded assembly: the test project holds
  `PlainGame.Damage` and the KindGame `Damage`, and the library needs one catalog per world.
- Reached kinds reuse `TimelineKindBake`/`TimelineKindBakeOwner` and `TimelineKindBakingSystem` (disables `Active`, checks columns), because a
  Baker can only enable generic types. Phase 7 must keep them when it deletes `TimelinePlayerBaker`.
- A director that also has `TimelinePlayerAuthoring` is left to the legacy baker.
- `TlTimelineSettings` is added from the Timeline marker row menu (`Tl Settings`); the timeline Inspector section, the Tl foldout, Also Plays
  and `GameObject > Tl > Timeline Object` are not built. Off-grid clips are rounded without a clip message. Changing the tick rate does not
  rebake (a `ScriptableSingleton` cannot be a `DependsOn` target).

**Entities Graphics (S5 rendering piece).** `com.unity.entities.graphics` 7.0.0 is now a package dependency. It draws only under an SRP
(`EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem` is false without one; the log says "No SRP present"). A project made with
`-createProject` uses the Built-in pipeline, so the cube moves but is invisible; the sample builder then logs a plain warning. With URP set
(the Hub's Universal 3D template; reproduced headless with `UniversalRenderPipelineAsset.Create` in the scratch project) the cube renders.

**Test A, headless** (fresh `-createProject` project, default modules + `com.tl.unity` from a local path + test framework + URP, the sample
copied into `Assets/` as Package Manager does, no testables):

| Check | Result |
|---|---|
| Compile with the five Force files (no asmdef) and the sample | 0 `error CS`, 0 warnings |
| Builder (`ForceDemoBuilder.Build`, batch): camera, light, ground, SubScene with a blue cube, Playable Director on `Push.playable`; track made from the generated `ForceClip_TlMenu` stub through `TlTrackSwap` | scene, SubScene and timeline written by Unity |
| Second batch process (editor restart) reads `Push.playable` | `TL-RESTART PASS`: sub-assets `TlClip, TlTrack, TimelineAsset` (no stub saved), track `Force`, template and data `Tl.Generated.ForceClip_TlData`, Strength 4, Direction (1,0,0), 2 curve keys, duration 2 s, 0 `m_Script: {fileID: 0}` |
| PlayMode test in the default world | entity has `Force`, `ForceLog`, `ForceFalloff`, `ForceKindEntry`, `TlTimelineSet`; Play On Awake starts Push; x 0 → 4.0333 (predicted 4 × Σ(1 − k/120)/60 = 4.0333), y 0.5, z 0 |
| Camera picture (batch Editor with a D3D12 device, `Camera.Render` into a RenderTexture) | cube at x 0 in the first picture, gone from there and at x 4.03 in the last: [start](phase4/force-demo-start.png), [end](phase4/force-demo-end.png) |

Not verifiable headless (spike S3): the real Timeline window, its `+` menu, Undo/Redo by keyboard, duplicate, copy and drag. The EditMode
test `TheMenuStubBecomesARealForceTrackInOneUndoStep` shows one `Undo.PerformUndo` removes the swapped track, but the window flow is the
owner's [3-minute check](../FORCE-DEMO-CHECK.md).

Open for phase 5: `TlBaker.AddBlob` warns on every second clip even when the value is equal (two Force clips print "Another clip ... already
sets ForceFalloff"); live edit and the tick-kept swap; the settings Inspector section and the director foldout.

## 11. Phase 5 results (live edit, Edit-mode preview, Inspector)

Built on 7000.0.0a16. Design notes, what was taken from BovineLabs Timeline and TweenPlayables, and what preview cannot show:
[live-preview.md](live-preview.md).

| Name | Where | What |
|---|---|---|
| `TlTimelineRegisterSystem` | `Runtime/Library` | also re-registers sets whose buffer changed and holds id 0 (a live-baking patch); change filter, 0 B per frame |
| `TlLibrary.Swapped`, layout check | same | a known timeline keeps its id; a version that reaches other kinds, or another kind catalog, is refused with one console error and the old one keeps playing |
| `TlTimelineSwapJob`, `KindCursor.Purge` | same, `Runtime/KindTick.cs` | Burst job run once per swap: each playback of a swapped timeline keeps its tick (clamped), its kind entries are dropped and rebuilt at that tick |
| `TlBaker.AddBlob` | `Runtime/Api` | warns only when a later clip sets a different value (byte compare); two equal curves are one blob |
| `TlPreviewMixer`, `TlPreviewHook`, `TlTrack.CreateTrackMixer/GatherProperties` | `Authoring/Timeline` | Timeline's own scrub calls into Tl; the director object's Transform is restored when preview ends |
| `TlPreview` | `Editor/Timeline` | bakes the director object (cached by dirty counts), steps a private world with Tl's systems and every `TlSimulationGroup` system to the playhead tick, writes `LocalTransform` back under `AnimationMode` |
| `TlAlsoPlays` | `Authoring/Timeline` | list of extra timelines, edited in the Tl foldout (component hidden in the Inspector); baked after the Playable, before the Next closure |
| `TlDirectorEditor` (Tl foldout under Unity's Playable Director), `TlTimelineSection` (Tl section under the Timeline asset header), `TlInspectorModel` | `Editor/Timeline` | Also Plays, what the object plays, plain messages (no timeline, not in a SubScene, no Tl tracks); Channel, When it ends, Play next, status, frame rate hint with a fix button; the settings marker is made on the first change, with Undo |

| Acceptance | Result |
|---|---|
| Edit Strength in Play (`ForceDemoLiveEditTests`, demo project, real live baking of the open SubScene) | patched and re-registered in 0.037 to 0.040 s, id 1 kept, tick 46 kept, push speed 3.85 to 12.97 |
| Swap in a hand-built world (`ALiveEditSwapsTheTimelineAndKeepsEachPlaybacksTick`) | same id, one reload, tick kept, one Force span, keeps playing |
| Adding or removing a track type in Play (`ALiveEditThatChangesTheTrackTypesIsRejectedWithAMessage`) | one console error, no reload, the old version keeps playing |
| Edit-mode scrub (`ForceDemoPreviewTests`, no Play) | ticks 30/60/90: 1.8083/3.0500/3.7917 in preview and in Play; end 4.0333 both; Strength 8 previews 8.0667; Undo back to 4.0333; cube restored to x 0; SubScene not dirty; [picture](phase5/force-preview-end.png) |
| Two equal clips | no warning; a different curve warns once |

Not verifiable headless: the real Timeline window scrub, the foldout and section drawn in a real Inspector, Ctrl+Z in the window. Those are
lines 14 to 19 of [FORCE-DEMO-CHECK.md](../FORCE-DEMO-CHECK.md).

## 12. Phase 6 results (control from code and clips)

Built on 7000.0.0a16. tl main a93a3298 adds `RequestOp.Pause`/`Resume` (`PlaybackTable.Pause/Resume`, `Requests.Pause/Resume`, plain host
honours them; tl tests 893 + 75 green); `external/tl-kinds` moved there, Core and the in-process packer resynced, generator dlls rebuilt.

| Name | Where | What |
|---|---|---|
| `TlTimeline { Hash128 Asset }` | `Runtime/Api`, namespace `Tl` | unmanaged reference to a Timeline asset, usable in clips, components and jobs |
| mirror rule | `Tl.Gen.Unity` | a `TlTimeline` or `TimelineId` field of a `[TlClip]` struct is a Timeline object field in the clip Inspector (drag and drop); `TlTimeline` bakes to the asset hash, `TimelineId` to a local id that the library patches |
| `TlRequests.Start/Stop/Pause/Resume(this DynamicBuffer<TimelineRequest>, in TlTimeline)` | `Runtime/Library` | plain buffer appends, callable from Burst `IJobEntity`/`IJob` code; `TimelineRequest.Asset` carries the reference |
| `TlRequestResolveSystem` | same | Burst `ISystem` first in `TimelineSimulationGroup`; a Burst job turns assets into library ids through `TlLibraryIndex` (change filter, idle when nothing was requested); an asset the object cannot play is dropped with one plain warning |
| Pause/Resume | `KindCursor.Apply`, `TimelineKernel.ApplyPause` | set and clear `PlaybackFlags.Paused`; a paused playback holds its tick and keeps its active spans, like the plain host |
| closure | `TlDirectorTimelines.Collect`, `TlInspectorModel.Plays` | timelines named in clip fields join the object's set (after the Playable and Also Plays), with `DependsOn` |
| `TlTimelineBlob.RefSites`, `TlLibraryPart.Refs` | `Runtime/Library` | offsets of `TimelineId` values inside the TLB with their local id; the assembler writes library ids there (and 0 until the target is loaded), like `Next` |
| `TlBaker.AddComponent<T>(T value)` | `Runtime/Api` | value overload; same first-wins rule as `AddBlob` |

The packer finds the `TimelineId` sites by baking the timeline twice with the local ids and with the ids marked (`| 0x8000`) and keeping the
offsets where exactly those values differ; the bake is deterministic, so the sites are exact.

| Acceptance | Result |
|---|---|
| `ClipTimelineFieldsJoinTheSetAndTimelineIdsArePatchedToLibraryIds` | set Push, Dash, Kick; one site with local id 1; the assembled library holds Push with Kick's library id; the `TlTimeline` field bakes to Dash's asset |
| `AJobStartsPausesAndResumesATimelineFromItsTlTimelineField` | a `[BurstCompile] IJobEntity` starts Dash when a gameplay flag is set; its `[BurstDiscard]` probe never ran (Burst code); Pause holds the tick for 5 steps, Resume continues |
| `StartingATimelineTheObjectCannotPlayWarnsAndStartsNothing` | one warning, nothing started |
| Control demo (sample, `Dash When Far` clip + `StartDashWhenFar` Burst `IJobEntity`) | Dash starts at x 2.05, cube ends at x 4.0333, z 3.0500 |

The five Force contract files are unchanged; the Control demo adds one optional file (`Control/DashWhenFar.cs`) to show the API.
