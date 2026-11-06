# Unity Timeline bridge (`com.tl.unity`)

Author Tl timelines in Unity's own Timeline window, then bake them into the
same library (`library.json`, timeline json, `game.tlib`) that the kinds
systems already run. Nothing new runs at play time: no entity per clip, no
timer entity per director, no extra systems. An entity carries its
`Playback` buffer and exactly its kinds, as in [KINDS.md](KINDS.md).

Runs on the newest Unity 7 editor (tested on 7000.0.0a16) with
`com.unity.timeline` 6.7.0, which `com.tl.unity` now depends on.

## How it works

```
Timeline window (.playable)          Timeline Bridge asset           Library folder
  Tl tracks, clips, markers   --->   name, end, next, channel  --->  <name>.json  (path 1)
                                     Bake (menu, button, save)       library.json "timelines"
                                                  |                  game.tlib, Timelines.g.cs
                                                  +--> tlb library ---^
                                                  +--> tlb kinds   ---> Kinds.g.cs (only if a capacity changes)

PlayableDirector + Timeline Director ---> fills Timeline Player names ---> existing Timeline Player baker
```

The bridge only writes Tl's existing authoring inputs and runs `tlb`. `tlb`
stays the one packer; the runtime loads `game.tlib` exactly as before.

## Assemblies

| Assembly | Folder | Holds |
|---|---|---|
| `Tl.Unity.Timeline` (runtime) | `Authoring/TimelineBridge` | `TlTrack<T>`, `TlClip<T>`, `TlMarker<T>`, `TimelineBridgeAsset`, `TimelineDirectorAuthoring` |
| `Tl.Unity.Timeline.Editor` | `Editor/TimelineBridge` | converter (`TimelineJson`), bake (`TimelineBridgeBake`), director baker, inspectors |
| `Tl.Unity` (runtime) | `Runtime/TimelineBridge` | `TimelineClips`: read clip data from your own Burst jobs |

Both bridge assemblies compile only when `com.unity.timeline` is installed
(asmdef `versionDefines` set `TL_UNITY_TIMELINE`). `Tl.Unity` does not need
Timeline.

## Author a timeline in the Timeline window

### 1. Declare the Unity side of your track, clip and marker types

Your game already has unmanaged Tl data types (`WalkTrack`, `WalkClip`,
`StepMarker`, ...) and the `ITrack`/`IMarker` declarations that feed kinds.
Give each a one-line Unity type in a runtime assembly that references
`Tl.Unity.Timeline` and `Unity.Timeline`. One class per file, file named after
the class (Unity needs that to save the assets):

```csharp
using UnityEngine.Timeline;

[TrackClipType(typeof(WalkClipAsset))]
public sealed class WalkTrackAsset : Tl.Unity.TlTrack<WalkTrack> { }

public sealed class WalkClipAsset : Tl.Unity.TlClip<WalkClip> { }

public sealed class StepMarkerAsset : Tl.Unity.TlMarker<StepMarker> { }
```

Your data structs need no `[Serializable]` and no change. The bridge stores
each public field as text and its inspector draws a typed field (number,
bool, enum popup) for each one. In code, `asset.Data` reads and writes the
struct.

### 2. Draw the timeline

1. `Create > Timeline`. Open it in the Timeline window.
2. Set the Timeline frame rate (Timeline window settings) to the library
   `tickRate` (60 in the test library) and keep snapping on. Then one frame
   is one tick and nothing is rounded.
3. Add Tl tracks (`+ > WalkTrackAsset`), select the track and fill its data
   in the Inspector. Add clips, place and size them, fill clip data.
4. Markers: right click the marker row or a track, `Add StepMarkerAsset`.
5. For a timeline longer than its last clip, set Duration Mode to Fixed
   Length and type the duration.

### 3. Create the Timeline Bridge asset

`Assets > Create > Tl > Timeline Bridge`, one per library.

| Field | Enter |
|---|---|
| Library | The Timeline Library asset (see KINDS.md Step 1). Its Folder, Game Assembly, Namespace, Kinds File and Tlb are used. |
| Output | `Json In Library Folder` (path 1) or `Staged Json` (path 2). |
| Ids File | Project path for the timeline ids class (`Timelines.g.cs`). Empty keeps the `tlb` default. |
| Bake On Save | Bake again when a listed `.playable` is saved. |
| Timelines | One row per Timeline asset: Timeline, Name (empty uses the asset name), End (`Remove`, `Loop`, `Hold`, `Next`), Next (a listed Timeline asset), Channel (a `library.json` channel name, empty stacks). |

### 4. Bake

Any of:

- the Bake button on the Timeline Bridge asset;
- `Tools > Tl > Timeline Bridge > Bake All Bridges`;
- saving a listed Timeline asset (Bake On Save).

A bake:

1. converts each listed Timeline asset to Tl timeline json;
2. writes it (path 1: `<Folder>/<name>.json`; path 2: a copy of the folder in
   `Temp/TlTimelineBridge/<bridge>`), and adds missing file names to the
   `library.json` `timelines` list (globs that already match are kept);
3. runs `tlb library <folder> --out <Folder>/game.tlib --assembly <game dll>...`
   (plus `--ids` and `--namespace`), with the compiled script assemblies
   staged as the capacity bake does;
4. when the library has a Kinds File, runs `tlb kinds` on the same folder,
   which rewrites `Kinds.g.cs` only when a capacity changes;
5. refreshes the AssetDatabase.

The Console shows one line per bake, the warnings, and the kind capacities.
Errors start with `Tl timeline bridge '<bridge>':` and name the timeline,
track, clip and time.

### 5. Put it on an entity

On a GameObject in a SubScene:

1. Add a `PlayableDirector` and assign the Timeline asset. Play On Awake
   means "start with".
2. Add `Timeline Director` (`TimelineDirectorAuthoring`). It adds a Timeline
   Player if there is none. Set Bridge, and list other Timeline assets game
   code may start in May Start.
3. Add your data components (as in KINDS.md), and `[RequiresKinds]` for kinds
   only game code pulses.

The Timeline Director fills the Timeline Player's Library, Start With and May
Start from the director and the bridge whenever you edit it. The existing
Timeline Player baker then bakes the `Playback` buffer, the start request and
exactly the reachable kinds. If the director changes afterwards and the
names no longer match, baking logs `Tl baking: GameObject '<name>' Timeline
Player does not match its PlayableDirector (...)`; run `Tools > Tl > Timeline
Bridge > Sync Directors` with the scene open.

## Mapping

| Unity Timeline | Tl | Notes |
|---|---|---|
| `TimelineAsset` | one timeline json, one manifest id | name from the bridge row or the asset name |
| `TimelineAsset.duration` (Based On Clips or Fixed Length) | `duration` (ticks) | 0 ticks or above 65535 ticks is an error |
| bridge row End `Loop` | `"loop": true` | |
| bridge row End `Hold` | `"hold": true` | |
| bridge row End `Next` + Next | `"next": "<name>"` | Next must be listed in the same bridge |
| bridge row Channel | `"channel"` | must be in `library.json` `channels` (`tlb` checks) |
| `TlTrack<T>` track, top to bottom, groups flattened | `tracks[]` entry, `type`/`namespace` of `T`, `data` from the track inspector | |
| `TlClip<T>` clip | `clips[]` entry, `start`/`end` in ticks, `data` | clips sorted by start |
| two overlapping clips on one track | Tl blend window (`IBlend`) | Tl's linear factor; Unity ease curves are not used |
| `TlMarker<T>` on any track or the marker row | `markers[]` entry, `tick`, `data` | sorted by tick, then authoring order |
| muted track | skipped, with a warning | |
| other tracks (Animation, Activation, Audio, Control, ...) and other markers/signals | ignored, with a warning | keep them for preview; they do not reach Tl |
| clip ease, clip-in, speed multiplier, pre/post extrapolation | not used | Tl clips have no such settings |
| `PlayableDirector.playableAsset` + Play On Awake | Timeline Player Start With | through `TimelineDirectorAuthoring` |
| `PlayableDirector` time, speed, wrap mode, bindings | not used | Tl advances one tick per step |

## Ticks

`ticks = floor(seconds * tickRate + 0.5 + 1e-9)`: the nearest tick, and an
exact half tick rounds up. The `1e-9` keeps a half that floating point
stores as 0.49999999999999994 a half. `tickRate` comes from `library.json`
(default 60). A time more than `1e-6` tick away from a whole tick still
converts but adds a warning that names the time and suggests setting the
Timeline frame rate to `tickRate`. Ticks are `ushort`, so 65535 is the most.
A clip that is shorter than one tick after rounding, or that ends after the
timeline duration, is an error; a marker at or after the duration is an error.

## Worked example: the Runner's `jump`

The test game's `jump.json` has three tracks and one marker. Built in the
Timeline window at 60 fps (or in code, as `TimelineBridgeTests.Jump()` does):

| Track | Track data | Clip | Frames | Clip data |
|---|---|---|---|---|
| ArcTrackAsset | Scale 1 | ArcClipAsset | 0 to 12 | Velocity 3 |
| WalkTrackAsset | Scale 1 | WalkClipAsset | 0 to 12 | Speed 1, Priority 0 |
| StatTrackAsset | Scale 1 | StatClipAsset | 0 to 1 | Delta -1 |
| marker row | | StepMarkerAsset | frame 6 | Delta 10 |

Bridge row: Timeline `jump`, End `Remove`, Channel `locomotion`. The bake
writes text equal to the hand-written `Tests/Kinds/Timelines~/jump.json`, and
`game.tlib` byte for byte equal to the one `tlb` packs from the hand-written
files. A GameObject whose director plays `run` with `jump` in May Start bakes
with `Lift+Locomotion+StatChange`, exactly as a Timeline Player that names
`Run` and `Jump`.

## Two paths, and which to use

1. **Json In Library Folder** (recommended). The Timeline assets are the
   source; the generated json sits next to hand-written json in the library
   folder and is committed. Diffs show what a designer changed in ticks, the
   .NET host, the plain C# host, CI and `tlb` on the command line see the
   same files, and the existing capacity-bake import hook keeps working.
2. **Staged Json**. The json exists only in `Temp/TlTimelineBridge`; the
   project receives `game.tlib` (and the ids and kinds files). Use it when
   the Timeline assets are the only source you want in version control.
   Other hosts then need Unity to rebuild the library.

Both paths call the same `tlb`, so both give the same `game.tlib` (tested).
A packer written inside Unity that skips `tlb` was not built: it would be a
second implementation of the library format to keep in step with `tlb`.

## Read timeline data from your own Burst jobs

Generated `[TimelineSystem]` jobs are not the only readers. Any job can read
the library and an entity's playbacks:

- the library: `SystemAPI.GetSingleton<TimelineLibrary>().View` (a
  `Tl.LibraryView`, plain pointers, valid until the library reloads); store it
  in a job field marked `[NativeDisableUnsafePtrRestriction]`;
- the playbacks: the `Playback` buffer (`Timeline` id, `Tick`, `Channel`,
  `Flags`);
- clip data: `TimelineClips.TryRead(in view, playback, pairKey, out TTrack
  track, out TClip clip)` returns the track and clip values active at the
  playback's tick (the first clip in a blend window);
  `TimelineClips.PairKey<TTrack, TClip>()` computes `pairKey` once on the main
  thread (it uses reflection, so not inside Burst). The key hashes the type
  names and their assembly name, as `tlb` does, so `game.tlib` must be packed
  against the assembly that declares the types in Unity (the bridge does
  this). The kinds test fixture `Tests/Kinds/Timelines~/game.tlib`, which
  the job tests load, is packed against `Tl.Unity.Kinds` by
  `eng/pack-kinds-fixture.sh`;
- kind entries: the generated `<Kind>Entry` buffers (`LocomotionEntry`, ...)
  and `<Kind>Active`/`<Kind>Enabled` are ordinary components, readable with
  `BufferLookup`, `BufferTypeHandle` or `DynamicBuffer` parameters.

`ISystem` + `IJobEntity` (tested in `TimelineJobAccessTests`):

```csharp
[BurstCompile]
public partial struct WalkSpeedJob : IJobEntity
{
    [NativeDisableUnsafePtrRestriction] public Tl.LibraryView View;
    public ulong WalkPair;

    void Execute(in DynamicBuffer<Playback> playbacks, ref WalkSpeed speed)
    {
        for (var i = 0; i < playbacks.Length; i++)
            if (TimelineClips.TryRead(in View, playbacks[i], WalkPair, out WalkTrack track, out WalkClip clip))
                speed.Value = clip.Speed * track.Scale;
    }
}

[BurstCompile]
public partial struct WalkSpeedSystem : ISystem
{
    ulong _walkPair;

    public void OnCreate(ref SystemState state)
    {
        _walkPair = TimelineClips.PairKey<WalkTrack, WalkClip>();
        state.RequireForUpdate<TimelineLibrary>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var view = SystemAPI.GetSingleton<TimelineLibrary>().View;
        state.Dependency = new WalkSpeedJob { View = view, WalkPair = _walkPair }.ScheduleParallel(state.Dependency);
    }
}
```

Order it after the Tick (`[UpdateInGroup(typeof(TimelineSimulationGroup))]`,
`[UpdateAfter(typeof(TimelineKindTickSystem))]`) to see this step's ticks.

`IJob` (tested): put the view, the key, a `Playback` and a
`NativeReference<T>` in the job and call `TryRead` in `Execute`.

`IJobChunk` in an `ISystem` (tested, `WalkSpeedChunkJob`): take
`BufferTypeHandle<Playback>` (read-only) and your component handle from
`SystemAPI`, loop `chunk.GetBufferAccessor(ref handle)` rows and call
`TryRead` for each playback.

## Tests

`Tests/TimelineBridge/EditorTests` (EditMode), all with Timeline assets built
in code:

- `run`, `jump`, `speed_boost`, `stomp` (fixed length, clips out of order),
  `vault` (next) and `waltz` (hold) convert to text equal to the hand-written
  json;
- tick rounding, off-grid warnings, skipped tracks, short clips and stale
  fields;
- `library.json` list patching and constant names;
- with `TL_TLB` set: both paths bake `game.tlib` equal to `tlb` on the
  hand-written json (ignored without `TL_TLB`);
- a director bakes exactly its kinds; an out-of-sync director is a baking
  error;
- a Burst `IJobEntity` in an `ISystem` writes a component from the clip data
  of each playback (2 for `run`, 5 once `speed_boost` overrides), a Burst
  `IJobChunk` in an `ISystem` does the same per chunk, and a Burst `IJob`
  reads one clip.

## Limits

- Data fields: numbers, `bool` and enums. `TimelineId`, nested structs and
  fixed buffers are not written; the inspector shows an error for them.
- Data types must be declared at namespace level (not nested).
- At most two overlapping clips per track (`tlb` rejects more).
- Only one level: Control tracks and nested timelines are ignored.
- No preview: scrubbing the Timeline window does not run kinds. Tl clips
  create empty playables.
- `TimelineClips.TryRead` returns the first clip of a blend window, not the
  blended value.

## Decisions

Simplest reversible choices, made without the owner:

1. `com.tl.unity` depends on `com.unity.timeline` 6.7.0 so the bridge and its
   tests are present by default; the bridge assemblies are still guarded by
   `versionDefines`, so removing the dependency only removes the bridge.
2. Timeline-level settings (name, end, next, channel) live on the bridge row,
   not on the Timeline asset, which has no place for them.
3. Ticks round to nearest, halves up, with a warning when off grid, instead of
   failing.
4. Non-Tl tracks and markers are ignored with a warning, so presentation
   tracks can stay in the same Timeline asset.
5. The director path fills the existing Timeline Player instead of adding a
   second baker, so kind sets have one implementation.
6. `TimelineClips` (in `Runtime/TimelineBridge`) is the one new runtime
   accessor; it reads the library rows that generated jobs already use.

## Compared with BovineLabs Timeline

[BovineLabs Timeline](https://gitlab.com/tertle/com.bovinelabs.timeline)
(read, not built or run) bakes each PlayableDirector to a timer entity, each
track to a track entity and each clip to a clip entity, and runs clock,
timer, local-time, weight and blend systems over them every frame.

It has, and this bridge does not: per-director clocks (pause, scale, reverse,
ranges), clip extrapolation (loop, ping-pong, hold), weighted blending with
mixers, nested timelines, track bindings to other entities, cue and crossing
streams, and Timeline window preview in an editor world.

This bridge has, and it does not: no entities per clip or track, so a running
timeline costs one `Playback` entry and the kinds' entries on the entity that
plays it; fixed-step deterministic ticks with cross-host hashes; kind
combine rules (priority, sum, each) across timelines and game code; channels
and next/hold chains; exact per-entity kind sets and capacity bakes; and the
same `game.tlib` for Unity, .NET and plain C# hosts.
