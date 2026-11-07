# Tl kinds in Unity (`com.tl.unity`)

How game-defined per-step work runs on the Unity host: the kind model, the
step order, authoring in a SubScene, sizing (kind sets, capacities), the
capacity bake, the Kind Report, and the samples with their cross-host hashes.

## Kind model

A kind is a value type plus a `Combine` rule (or `Each`) plus an `Apply`
method. A clip feeds a kind through a `[Feeds]` method evaluated every step
while the clip is active; a `[Pulse]` method fires a one-step value at clip
start, clip end, or a marker. Game code sends one-step values through
`KindPulses<K>.To(entity, value)`. The test game (`Tests/Kinds/Game`) defines:

| Kind | Value | Combine | Apply target | Fed / pulsed by |
|---|---|---|---|---|
| Locomotion | `Speed` (`Value`, `Priority`) | highest priority wins | `Motion.X` | `Walk.Evaluate` (reads `WalkStat`, `Target`, `AnchorLookup`) |
| Lift | `float` | sum | `Motion.Y` | `Jump` fold + `Evaluate` (reads `JumpPower`) |
| Damage | `int` | sum | `Health` | `Hit` start pulse; game code via `KindPulses<int>.To` |
| StatChange | `int` | each applied once | `Stamina` | `ChangeStat` start pulse, `Steps` marker pulse |

`Locomotion` shows overriding by priority (a faster high-priority entry
shadows the walk until its clip ends). `Lift` shows stacking (jump plus two
potion hops sum into one value per step). `Damage` shows game-code pulses
summed with timeline hit pulses into one `Health` write per step. `StatChange`
shows `Each` mode: every pulse applies exactly once, in a fixed merge order.
`Whistle` raises a `Cue` presentation event (see Presentation events).

A kind is named by its type name (`Damage`). That name is what the authoring
fields, `[RequiresKinds]` and `library.json` use.

## Step order

Each step runs `TimelineKindTickSystem` first, then the kind loops inside each
system (`Movement` runs Locomotion, Lift, StatChange; `Vitals` runs Damage):

1. `Tick`: apply start/stop requests, advance playback ticks, maintain span
   entries (span start/end from boundaries, marker pulses), merge queued game
   pulses in a deterministic order, handle end/next/hold/finish, maintain the
   `Active`/`Enabled` row state.
2. Kind loops: one loop per kind per chunk over that kind's entry buffer.
   Rows whose kind row is disabled (`Enabled` off, or `Active` off) contribute
   nothing but keep their entries.
3. `Apply`: each kind writes its owner's data (`Motion`, `Health`, `Stamina`).

Every Tl system on the step path (`TimelineRequestSystem`,
`TimelineAdvanceSystem`, `TimelineKindTickSystem`, `TimelineEventSystem`) is a
Burst `ISystem`. Everything a step needs is in unmanaged singletons:
`TimelineLibrary` (`View`, the `KindIndex` in `Kinds`, and `Generation`, which
changes on a hot reload) and `TimelineStep` (`Forward`, the step direction;
set it with `SystemAPI.SetSingleton`). Your kind systems run after the Tick,
also as Burst `ISystem`s:

```csharp
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
```

`Tests/Kinds/Harness/KindWorld.cs` has the full `Movement` and `Vitals` pair.

An entity that carries `TimelinePlaying` belongs to the kind Tick.
`TimelineRequestSystem` and `TimelineAdvanceSystem` skip it. The Timeline
Player bake adds `TimelinePlaying` (disabled) to every player; the Tick
enables it when it applies a request.

Send game pulses before the step; they merge during that step's Tick. Take
the `TimelineKindTick` singleton (`SystemAPI.GetSingleton<TimelineKindTick>()`,
it lives on the Tick system's entity), call `BeginPulses(n)`, write with
`DamageKind.Pulses(sender.Writer).To(target, value)` (from Burst code or a
job; pass the job's handle to `AddPulseWriter`). Pulses to an entity without
the kind are counted in `DroppedPulses` and dropped, as are starts for
unreachable timelines (`DroppedStarts`), both on `TimelineKindTick`. A count
above zero usually means a missing `[RequiresKinds]`, Kinds list entry or May
Start name (see Authoring).

## Authoring in a SubScene

A GameObject runs kinds when a Timeline Player is baked onto it. The baker
gives the entity the playback buffers and exactly the kinds it can reach.
Baking only runs for GameObjects in a SubScene.

To draw timelines in Unity's Timeline window instead of writing their json,
and to start them from a `PlayableDirector`, see
[TIMELINE-BRIDGE.md](TIMELINE-BRIDGE.md). It writes the same library files
and fills the same Timeline Player.

Before you start you need:

- A game assembly that declares the kinds, feeders and pulses. Its asmdef
  references `Tl.Unity` and `Tl.Unity.Authoring` (the attribute, the
  components and the assets live in `Tl.Unity.Authoring`, a runtime
  assembly), plus `Tl.Unity.Generate`. `Tests/Kinds/Tl.Unity.Kinds.asmdef`
  lists the other references.
- The kind code is generated at compile time by `Tl.Gen.Unity`, a Roslyn
  source generator that ships prebuilt in the package
  (`Generators/Ecs/Tl.Gen.Unity.dll`, imported as a `RoslynAnalyzer` for no
  platform). Unity runs it only for assemblies whose asmdef references the
  marker assembly `Tl.Unity.Generate`, so the package's own assemblies and
  your other assemblies are never touched. The package's test game checks in
  its generated files instead and does not reference the marker.
  `eng/build-generators.sh` rebuilds both shipped generators (this one and
  the plain host's `Tl.Gen.Plain`) from the pinned `external/tl-kinds`
  sources; `--check` fails when a shipped dll is stale.
- A folder with `library.json`, the timeline `.json` files and the baked
  `game.tlib`, plus the generated ids class (one `ushort` constant per
  timeline, named in PascalCase: `potion_hop.json` gives `PotionHop`).
  `tlb library <folder>` writes `game.tlib` into the folder, and
  `Timelines.g.cs` and `Kinds.g.cs` beside it. Pass `--ids` and `--kinds` to
  put them where your game assembly expects them, and use the same file for
  `--kinds` as for the library's Kinds File, so only one `KindCapacity`
  exists. Its options are in the tl repository, `docs/library-layout.md`.
- The game's data components that the kinds' systems read and write (in the
  test game, `RunnerAuthoring` bakes `Movement_MotionData`,
  `Vitals_HealthData` and the other columns).

### Step 1: create the Timeline Library asset

`Assets > Create > Tl > Timeline Library`. One asset per timeline folder.
Fill its fields:

| Field | Enter |
|---|---|
| Folder | The folder with `library.json`, the timeline files and `game.tlib`. Project-relative; `Packages/...` paths work. |
| Timeline Ids | Full name of the generated ids class, for example `Game.Timelines`. |
| Game Assembly | Name of the script assembly with your kinds, for example `Game.Logic`. |
| Namespace | Namespace of the generated `KindCapacity` class. Default `Game`. |
| Kinds File | Project path where the capacity bake writes `Kinds.g.cs`. Leave empty to skip the capacity bake for this library. |
| Tlb | The `tlb` command, or the path to `tlb` or `Tl.Bake.dll`. |
| Kind Sets | `Exact` (default) or `Union Per Profile`. See Step 4. |

Timeline Ids, Game Assembly and Folder decide what the Timeline Player can
resolve. Namespace, Kinds File, Game Assembly and Tlb matter only for the
capacity bake.

### Step 2: add a Timeline Player

Select a GameObject in a SubScene. `Add Component`, search for Timeline
Player (the component is `TimelinePlayerAuthoring`). There is one per
GameObject.

| Field | Enter |
|---|---|
| Library | The Timeline Library asset from Step 1. |
| Start With | Timeline names started when the entity spawns, in list order. |
| May Start | Timeline names game code may start on this entity later. |
| Kinds | Kinds game code sends pulses to on this entity (see Step 3). |
| Profile | A Kind Profile. Used only with `Union Per Profile` (see Step 4). |

Timeline names are the constant names of the Timeline Ids class. Kind names
are kind type names. A name that does not exist is a baking error.

The baker puts this on the entity:

- `Playback` and `TimelineRequest` buffers, with one start request per Start
  With name.
- `TimelinePlaying`, disabled.
- For each reached kind: its entry buffer, `<Kind>Active` (disabled) and
  `<Kind>Enabled` (enabled).

A kind is reached when it is:

- in the library kind mask of a Start With or May Start timeline, or of any
  timeline on that timeline's `next` chain;
- in the Kinds list;
- named by `[RequiresKinds]` on another component on the same GameObject;
- in the Profile, with `Union Per Profile`.

Kinds that are not reached are not on the entity. A start for a timeline
whose kinds the entity lacks is dropped (`DroppedStarts`). Starting a May
Start timeline is game code's job: add a `TimelineRequest.Start` to the
entity's request buffer.

The baker depends on the Library and Profile assets, so editing them bakes
again. It does not depend on `game.tlib`. After rebuilding `game.tlib`,
reimport the SubScene.

### Step 3: name the kinds game code pulses

A timeline reaches its own kinds. A kind that only game code pulses (such as
`Damage` from a bullet) has no timeline on that entity, so name it.

Preferred: `[RequiresKinds]` on a component on the same GameObject. It takes
the kind types, so a renamed kind is a compile error:

```csharp
using Tl.Unity;
using UnityEngine;

[RequiresKinds(typeof(Damage))]
public sealed class DamageableAuthoring : MonoBehaviour
{
}
```

`[RequiresKinds(params Type[])]` is in namespace `Tl.Unity`, applies to
classes and is inherited. Several kinds go in one attribute.

For a one-off, type the kind name in the Timeline Player's Kinds list.

### Step 4: share archetypes with a profile (optional)

With `Exact`, each entity carries exactly its reached kinds, so entities with
different kind sets are in different archetypes. That is the least memory and
the most archetypes. To make several kinds of GameObject share one archetype:

1. On the Timeline Library asset, set Kind Sets to `Union Per Profile`.
2. `Assets > Create > Tl > Kind Profile`. Fill Kinds with kind names.
3. Set the Profile field on each Timeline Player that should share.

A player with a Profile gets its reached kinds plus every kind of the
profile. Players without a profile stay exact. With `Exact` the Profile field
is ignored. If a player reaches a kind its profile lacks, the bake warns and
that player gets its own archetype. Fewer archetypes cost more bytes per
entity; the Kind Report shows both.

### Step 5: read baking errors

Messages start with `Tl baking: GameObject '<name>'`. The bake keeps going
and the entity is baked with what could be resolved.

| Message | Fix |
|---|---|
| `has no Library.` | Set the Library field. |
| `library '<name>' names timeline ids class '<class>', which is not compiled.` | Fix Timeline Ids and Game Assembly, or generate the ids class. |
| `start-with names timeline '<name>', which <ids class> does not declare.` | Fix the name (also `may-start`). |
| `the Kinds list names kind '<name>', which no Unity system runs.` | Use the kind type name. The same text appears for `[RequiresKinds]` and profile names. |
| `timeline '<name>' feeds kind '<kind>', which no Unity system runs.` | The library has a kind the compiled game code does not register. Recompile the game assembly and rebuild `game.tlib` from the same code. |
| `gets kind '<kind>' from <source>, fed by <feeders>, but has no '<column>' component.` | Add the component whose baker adds that column to the same GameObject. `<source>` says why the kind was reached; `<feeders>` is `game code` when no timeline feeds it. |
| `: Timeline library '<name>' has no game.tlib in '<folder>'.` | Bake `game.tlib` into Folder. |

One warning exists: `reaches kind '<kind>' through <source>, which profile
'<profile>' lacks, so it gets its own archetype.` Add the kind to the profile
or accept the extra archetype.

Example, from `Tests/Kinds/EditorTests/KindBakingTests.cs`:

```
Tl baking: GameObject 'Bare' gets kind 'Locomotion' from timeline 'Run', fed by Walk.Evaluate, but has no 'Movement_MotionData' component.
```

The column check runs after all bakers, so the component can come from any
baker on that GameObject.

### Step 6: load the library at runtime

The world needs the library before the first step. Hand the bytes over once,
at startup:

```csharp
TimelineLibraryBootstrap.Use("game.tlib", bytes);
```

`bytes` is the content of `game.tlib`. `TimelineLibrarySystem`
(`InitializationSystemGroup`) then loads it into every world it runs in and
creates that world's `TimelineLibrary` and `TimelineStep` singletons; the
library is freed with the world. Loading is the only managed step; no system
of yours calls managed code per world or per step. `Release()` forgets the
bytes. A world built by hand without `TimelineLibrarySystem` can load directly
with `TimelineLibraryBootstrap.Ensure(entityManager, "game.tlib", bytes)` and
`Shutdown(entityManager)`. `Tests/Kinds/Game/KindLibraryBootstrap.cs` calls
`Use` from a MonoBehaviour next to the SubScene; the test game embeds the
bytes in a generated class.

Kind names must be unique across assemblies. Two assemblies that both declare
a `Damage` kind fail at load with `Kind 'Damage' is registered twice: by
assembly 'A' and by assembly 'B'.` The package's own test game
(`Tests/Kinds`, assembly `Tl.Unity.Kinds`) compiles only when the package is
listed in `testables`, so it never collides with your kinds.

A kind's id (`DamageKind.Id`, the key a pulse carries) is the 32-bit FNV-1a
hash of its name, `KindIndex.IdOf("Damage")`, so it does not depend on how
many assemblies declare kinds or on the order they load. The library stores
kind names, and the runtime maps each id to the library's kind when it binds
the library. Two names with the same id fail at load with `Kinds 'A' and 'B'
have the same kind id`. The package tests prove it with a second kind-bearing
assembly (`Tests/Generated`, kind `Gust`) loaded next to `Tl.Unity.Kinds`: the
600-step and events hashes do not change.

## Sizing

Every entity pays a header plus inline entries for each kind it can reach.
Step 2 assigns exactly the reachable kinds, so in the test game a walker
carries Locomotion only. Kind sets (Step 4) trade archetypes against bytes
per entity.

Each kind's capacity comes from `Kinds.g.cs`: one `const int` per kind in
`KindCapacity`, used as the `[InternalBufferCapacity]` of the kind's entry
buffer. `tlb kinds` computes it as the worst case on one entity (span entries
from the timelines, timeline pulses, plus `gamePulses` for game-code pulses),
capped by `maxInlineBytes`. `inline` sets a capacity directly. These are
set per kind in the `kinds` section of `library.json`; the test library has:

```json
"kinds": {
  "Damage": { "gamePulses": 8, "maxInlineBytes": 132 }
}
```

and `Tests/Kinds/Game/Kinds.g.cs` pins Damage 11, Lift 2, Locomotion 2,
StatChange 3. The capacity rules and the `kinds` keys are in the tl
repository, `docs/library-layout.md`, section "Kinds (library version 2)".

Rules of thumb: tens of kinds, not hundreds; capacity 0 for rare kinds; let
the report guide the rest.

## Capacity bake

The capacity bake keeps `Kinds.g.cs` current. It is an Editor step; the
game does not run it.

Setup:

1. Use a `tlb` that has the `kinds` command. `tl.bake` 2.0.1 on NuGet is too
   old. Build `tools/Tl.Bake` in the tl repository, or use a newer package.
2. On the Timeline Library asset, set Tlb to the `tlb` command, or to the
   path of `Tl.Bake.dll` (it is run with `dotnet`). Set Game Assembly,
   Namespace and Kinds File.
3. `Tools > Tl > Bake Kind Capacities`.

For each Timeline Library asset in the project that has a Kinds File, the
bake copies the compiled script assemblies into `Temp/TlKindBake` and runs:

```
tlb kinds --library <Folder> --namespace <Namespace> --out <KindsFile> --assembly <staged game dll> --diagnostics json
```

`tlb` rewrites `Kinds.g.cs` only when a capacity changes. When it did, the
bake refreshes the AssetDatabase and scripts recompile. Otherwise nothing
changes and nothing recompiles. Either way the Console lists each kind: its
capacity, its worst case, what set it, and its inline bytes.

The bake also runs by itself:

- after every script reload;
- when a `.json` file in a library's Folder is imported, moved or deleted.

After a capacity changes, the reload it causes runs the bake again and finds
nothing to write. A Folder whose name ends in `~` is not imported by Unity,
so the `.json` trigger does not fire there; use the menu. Libraries with an
empty Kinds File are skipped.

Until `Kinds.g.cs` exists, the generator warns `TLUNITY9103` and inline
capacity is 0. The first bake writes the file.

Failures are logged as `Tl kind capacities: library '<name>': <message>` and
leave `Kinds.g.cs` as it was. The usual ones:

- `Script assembly '<name>' is not compiled yet.` Fix Game Assembly, or wait
  for the compile.
- `could not run '<tlb>' ...` Fix the Tlb field.
- `tlb exited <code>: ...` The text is tlb's own error (a missing
  `library.json`, an unknown `kinds` key, and so on).

The EditMode test `EditingATimelineWithoutChangingACapacityRewritesNothing`
reads `TL_TLB` (the same value as the Tlb field) and is ignored when it is
not set.

## Kind Report

`Tools > Tl > Kind Report` logs what the kinds cost in the entities that
exist. To use it:

1. Open a scene whose SubScene bakes Timeline Players, or enter Play mode.
2. Run the menu item.
3. Read the Console. There is one block per world that has entities with
   kinds. A block that has warnings is logged as a warning.

Each row is one archetype that carries at least one kind:

```
<kinds>: <entities> entities in <chunks> chunks of <chunk capacity>, <n> kinds, <bytes> B kind storage per entity
```

`<kinds>` are the kind names joined with `+`. Kind storage is the size the
kind entry buffers take in the chunk, per entity. Compare `entities` with
`chunks` times `chunk capacity`: many mostly empty chunks mean too many
archetypes, and a profile merges them.

Three warnings:

- More than 32 kinds on one archetype. Give the entity fewer kinds, or split
  the entity.
- More than 1024 B of kind storage per entity. Lower capacities, give rare
  kinds capacity 0, or take kinds out of a profile.
- Fewer than 16 entities per chunk. Per-entity storage is large; fix it as
  for the 1024 B warning.

With no entities that carry kinds, the menu logs: no world has entities with
kinds.

The report logic is in the runtime assembly, so game code can call it:
`KindReport.Capture(entityManager, KindRegistry.Discover())` returns the
rows, `KindReport.Warnings(rows)` the warning strings, and
`KindReport.Text(worldName, rows)` the text above. The limits are
`KindReport.MaxKinds`, `MaxKindBytes` and `MinChunkCapacity`.

## Samples

Both samples live in `com.tl.unity/Samples~`, run headless (`-batchmode`
quits 0 on match, 3 on mismatch), and print `hash expected match backend`
receipts. They run the package's test game, so they compile only with
`"testables": ["com.tl.unity"]` in `Packages/manifest.json`, and their build
scripts pass `BuildOptions.IncludeTestAssemblies`.

- `KindsIL2CPP` (`KindsSample`): the Runner on kinds. A speed boost overrides
  the normal walk by priority (X 2 then 7, two entries, 102 over 19 steps,
  run takes over at 104 with one entry), lifts add up (Y 44, then 84 with a
  stacked potion hop), stat pulses apply once (Stamina -1, then 9 from the
  marker). Then the 600-step random script: hash `13943614770702343869`,
  equal to the .NET `KindUnityHashTests` run.
- `BulletsIL2CPP` (`BulletsSample`): game-code `Damage` pulses plus a `Stomp`
  timeline's hit pulses summed into one `Health` write per step (993 after the
  game pulse, 955 after two game pulses land with the timeline hit, holds at
  955, 945 on the second hit, untouched rows stay 1000, zero dropped
  pulses). Step hash over all four rows' `Health`: `3081412629212866412`,
  reproduced from the .NET `KindRunTests` bullets script.

## Presentation events (`Events<T>`)

A consumer that raises presentation events declares `OnClipStart`,
`OnClipEnd` or `ExecuteActive` with a Frame and `ref Events<T>` parameters
only, and its system declares an `Events<T>` field (the test game's
`Whistle` and `Movement.Cues`):

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

On Unity the generated kind job of that system dispatches these methods in
Burst, with the same firing rules as the .NET host (`OnClipStart` on the
clip's first tick, `OnClipEnd` on its last tick and when the timeline is
stopped inside the clip, `ExecuteActive` on every tick inside it). Each
owner entity gets a `DynamicBuffer<TimelineEvent<T>>` (`Tl.Unity`); the
Timeline Player bake adds one for every event type the game declares. Every
step clears the buffer and refills it with the events that entity raised in
that step, ordered by playback slot, then by raise order (the order of the
.NET host's `Events<T>.Drain`). `TimelineEvent<T>.Record` holds `Slot`,
`Sequence` and `Value`; `Record.Entity` is not used on Unity. Read the buffer
in a system that runs after `TimelineSimulationGroup`.

The job writes the events straight into the owner's buffer: `Events<T>` is
built over the buffer's memory (`Events<T>(header, items, capacity)`), and a
step that raises more events than the buffer holds grows the buffer and runs
that entity's dispatch again, so no event is lost and the result is the same.
The system's dispatch only runs for chunks that carry the buffer.

The test `EventScriptRaisesTheSameCuesAsThePlainHost` plays a 600-step
script that starts and stops `whistle` among the other timelines and hashes
every entity's cues per step: `6607956939547305625`, equal to the plain C#
host (the .NET `Tl.Core` host) running the same script on the plain sample.
That script exercises `OnClipStart`; the `OnClipEnd` and `ExecuteActive`
paths follow the same rules but have no Unity test yet.

The test fixture `Tests/Kinds/Timelines~/game.tlib` (and `Timelines.g.cs`,
`Kinds.g.cs`, `KindLibraryBytes.g.cs` beside the game) is packed against the
Unity test game assembly `Tl.Unity.Kinds`, so its pair keys match the
generated code. Repack it after changing the game types or the json with
`TL_TLB=<tlb> eng/pack-kinds-fixture.sh <project>/Library/ScriptAssemblies`
(a project that compiled the package tests); `--check` fails when it is stale.

## Legacy mixing

Plan-1 `ExecuteActive` consumers keep plan 1's order inside one implicit
legacy kind that runs first in its system unless `Order` says otherwise.
Mixing legacy consumers and kinds that write the same field changes the order
between those two groups. On Unity the `Events<T>` raisers above
(`OnClipStart`, `OnClipEnd` and `ExecuteActive` whose only extra parameters are
`Events<T>`) are dispatched, in Burst, by the generated job, as in .NET. Other
legacy consumers (marker handlers, and those methods with column parameters)
are not dispatched on Unity (`TLUNITY9105`: feed a kind instead). The legacy implicit kind and
`ExecuteActive` stay (K5 decision, IAFahim/tl#558). They go only after the
Runner sample and the bench domains move to kinds, a migration tool exists,
and K-E still passes without the legacy loop.

## Not covered

The authoring, bake and report code is Editor-side code with EditMode tests
(`Tests/Kinds/EditorTests`). This page makes no claim about player builds,
other Unity versions or other `tlb` versions.
