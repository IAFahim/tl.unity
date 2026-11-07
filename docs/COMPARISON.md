# Tl kinds and the BovineLabs Timeline: an honest comparison

Date of measurements: 2026-11-06. Machine: AMD Ryzen 5 8500G, 12 threads
(11 job workers). Editor: the newest Unity 7 editor (tested on 7000.0.0a16),
Burst on, Entities 7.0.0 as shipped with that editor.

This page compares the design of `com.tl.unity` (the kinds model in
[KINDS.md](KINDS.md)) with the BovineLabs Timeline package
(<https://gitlab.com/tertle/com.bovinelabs.timeline>, by tertle). I read their
README, `Documentation~` and source. I did not build, import or run their
package. The speed numbers below come from a **model of their design**
written for this page, not from their package. Read "The limits of this
comparison" before quoting any number.

## Short answer

- At 100k timeline owners, one walk clip each, the Tl kinds step is
  **about 7 to 8 times cheaper per entity** than the modelled BovineLabs
  design when all entities play, **about 12 to 15 times** when 10% play, and
  **about 20 to 37 times** when none play. The fixed cost of an empty world is
  **about 3 to 5 times** lower.
- That is the cost of the timeline plumbing only. Your own clip evaluation
  code costs the same in both, so a game with heavy clip code sees a smaller
  ratio.
- Nothing measured here supports a claim of orders of magnitude. The real
  numbers are single-digit to low double-digit multiples.
- BovineLabs is ahead on authoring maturity, ecosystem, nested timelines,
  cue markers, clip extrapolation and editor preview. See the feature table.

## What each design is

**BovineLabs Timeline** bakes a Unity Timeline asset (authored in the Unity
Timeline window) into ECS entities at bake time: one director (timer)
entity, one entity per track, one entity per clip (`Documentation~/Architecture.md`,
`BovineLabs.Timeline.Authoring/ConversionContext.cs`, `ClipBaker.cs`). These
entities exist for the whole life of the timeline instance. Starting and
stopping is done with enableable components (`TimelineActive`, `ClipActive`),
not with structural changes. Per frame, systems run over those entities:

| System (source file) | What it does per frame |
|---|---|
| `ClockUpdateSystem` | Writes `ClockData` for active directors. |
| `TimerUpdateSystem` | Advances active directors and copies the time into every linked track and clip entity (`TimerDataLink` buffer, random writes). A second job visits **every inactive director** and clears the enable bits of its linked entities. |
| `ClipLocalTimeSystem` | Computes local time and sets `ClipActive` for clips of active timelines (change filter on `TimerData`). A second job writes `ClipActive = false` on every clip of an inactive timeline. |
| One system per track type, using `TrackBlendImpl` | Clears a `NativeParallelHashMap` keyed by the bound entity, sized from a clip count taken without filtering; fills it from active clips (parallel job for clips without a weight, a **single-threaded** job for clips with `ClipWeight`); a parallel job then writes each blended value to the bound entity. |
| `ClipActivePreviousSystem`, `TimelineActivePreviousSystem` | Copy the enable bits for every clip and timeline entity. |

**Tl kinds** keeps one entity per owner. The entity carries a `Playback`
buffer and one small buffer per kind it can reach. A start adds an entry to
the kind buffer, an end removes it. Each step runs one `Tick` over playing
playbacks, then one tight loop per kind over entities whose kind row is
active. Entities with nothing playing are skipped by an enabled bit
(KINDS.md, "Step order"; roadmap plan section 4.5).

### Correction to the roadmap plan, section 2.3

Section 2.3 of the kinds plan rejects "an entity per clip or per playback (the
BovineLabs approach)" and says it costs "structural changes on every start and
stop". The BovineLabs source does not do that. Its clip entities are baked
once and toggled with enable bits. The cost of its design is different: four
entities per owner for a one-clip timeline (director, track, clip, plus the
owner), random writes from the director to the linked entities, work on
inactive timelines every frame, and a hash map per track type. The rest of
the section (memory per clip per player, random writes back to the owner) is
accurate for their design. To check the structural-change point anyway, the
model below also times a hypothetical spawn and destroy of a clip entity at
every clip boundary. That variant is not BovineLabs.

## Feature table

"Tl" means `com.tl.unity` as of origin/main at the time of writing. "BL" is
the BovineLabs package at its public snapshot (`9f0de90`, version
`2.0.0-pre.7`, changelog from 1.0.0 on 2024-11-26). I could verify the BL
column from its docs and source; I did not run it.

| Area | Tl | BL | Ahead |
|---|---|---|---|
| Authoring | Unity Timeline window: one package `TlTrack` for every clip type, the `+` menu entry and the clip Inspector come from the coder's `[TlClip]` struct; a Playable Director in a SubScene bakes it ([UNITY-TIMELINE.md](UNITY-TIMELINE.md)). Edit-mode scrub preview runs the real Tl systems in a private world. | Unity Timeline window is the authoring tool; custom tracks and clips derive from `DOTSTrack` and `DOTSClip` (`Documentation~/Authoring.md`). Editor preview of DOTS state via `EditorTimelineSystem`. | BL: established, shipping workflow; Tl: no per-type track class. |
| Maturity and ecosystem | New (first commit in this repo 2026-09-10, package version 0.1.0, license not yet decided per the root README). Tested by EditMode tests and cross-host hashes. | Released since 2024, a prerelease 2.0 line, sample scene, own test suite, part of the wider BovineLabs package set (Core, Nerve). LGPL-3 with Unity-derived parts under the Unity Companion License. | BL. |
| Runtime model | One entity per owner, one buffer per kind. | Director, track and clip entities per timeline instance. | Depends on need. |
| Per-step cost | See measured table. | See measured table. | Tl, by the ratios measured below. |
| Blending | Per-kind combine rule (sum, highest priority, each-once, custom) and a per-track `IBlend` hook. | Weighted blend of up to four clips per bound entity, Timeline mix curves as clip weight, additive mode, mixers for float, float2, float3, float4, quaternion (`TrackBlendImpl.cs`, `JobHelpers.cs`, `Mixers/`). | BL for authored cross-fades. |
| Several timelines on one owner | Yes, each at its own tick; contributions meet in the kind combine rule. | Yes, by several directors bound to the same target; blend through the hash map. | Tie. |
| Markers and cues | Marker pulses per clip event and `IMarker` (jump.json has one). | `DOTS Cue Track`, `TimelineCueMarker`, swept `TimelineCrossing` stream with ordered cue and clip enter/exit events (`Runtime.md`). | BL for authored cue ordering. |
| Signals | Not a separate feature; pulses and markers feed kinds. | Not supported (`Limitations.md`: arbitrary signals and markers are not converted). | Tie. |
| Nested timelines | No nesting feature in the docs I read; `next` chains and several concurrent playbacks exist. | `SubDirectorTrack`, `SubTimelineClip`, composite timers with scale and offset (`Nesting.md`). | BL. |
| Clip extrapolation | Not a documented feature. | Hold, loop, ping-pong, pre and post (`ClipLocalTimeSystem.cs`). | BL. |
| Direction and rewind | Per-step direction and per-playback reverse flag; roadmap plan says events are tied to ticks so a rewind undoes them. | Reverse clock; swept crossings neither rewind nor re-fire on backward movement (`Runtime.md`). | Tl, on paper. I did not test Tl rewind here. |
| Determinism | Fixed merge order; the kinds samples print a hash equal to the .NET run (KINDS.md, "Samples"). | Integer `DiscreteTime`, activation generations, ordered crossings. No cross-host hash claim in the docs. | Tl has the cross-host receipt. |
| Networking | No netcode integration. | Timeline groups run in server, local-simulation and client-simulation worlds; no rollback feature in the docs. | BL has world coverage; neither has a rollback story I could find. |
| Game-code input | `KindPulses<K>` from any system, merged in a deterministic order. | Enable and request components on the director (`TimerActivation`). | Tl for pulses. |
| Hosts | .NET host and Unity host from one library; 0 B allocation per warm step. | Unity only. | Tl. |
| Requirements | Unity 7 editor (tested on 7000.0.0a16), Entities. | Unity 7000.0.0a11 or newer, `com.unity.timeline` 6.7.0, `com.bovinelabs.core` 2.0.0-pre.7, Entities 6.7.0 (`package.json`). | Tie. |

## Cost model: why the numbers come out this way

A timeline runtime pays for three things: work per instance that exists,
work per instance that plays, and work when something starts or stops.

**Per instance that exists (idle cost).** In BL every timeline instance owns
entities that are visited when it is idle. The timer system has a job over
every inactive director that resets state and writes the enable bits of each
linked entity through a lookup. The clip system writes `ClipActive = false` on
every clip of an inactive timeline. The track blend step clears a hash map
sized from a clip count taken without filtering, whether or not any clip is active. In Tl an idle
entity has its `Playing` enabled bit off and is skipped per chunk, so the idle
cost is a bit test per 128 entities.

**Per instance that plays.** In BL one step copies the director time into each
linked track and clip entity (random access, each link is another chunk),
computes local time per clip, collects active clips into a hash map keyed by
the target entity, then reads that map to write the target. That is three to
four passes and a hash lookup per clip. In Tl one pass per kind reads the
entry buffer of the owner and writes the owner's data in the same chunk.

**Per start or stop.** In BL a start or stop is an enable-bit flip on a few
entities, found by the time of the next step; there is no structural change.
In Tl a start or stop edits a small buffer on the owner. I also timed the
hypothetical case where each clip boundary creates and destroys an entity:
about 30 to 60 ns per destroy and create pair with the batch API. At 2.5% of
100k clips per step that is about 1 ns per owner per step, small next to the
per-step costs above. So structural churn is not what makes the entity-per-clip
shape expensive here; the per-frame visits are.

**Why grouping by kind helps.** All walking runs in one loop over one buffer
type, so the loop reads contiguous data and skips idle chunks with a bit. The
entity-per-clip shape reads data from four or more entity types in different
chunks and joins them again through a hash map.

From the model's cumulative timing (ns per owner, safety checks on, all
playing): clock and timer systems about 36, local time about 1.5, the walk
track system (evaluate, hash map, write) about 34, previous-state copies
within noise. With nothing playing: clock and timer about 15, the rest about
5.

## Measured table

All rows: 100,000 owners, one walk clip each (`run` timeline, loop of 40 ticks,
speed 2) unless noted, 10 warm-up steps then 200 measured steps, Editor batch
mode, Burst on. Values are ns per owner per step unless noted. Two separate
Editor launches, two rounds each; the value shown is the median of the two
second rounds (the first round is cold). Burst safety checks were on (the
Editor default), which is how the earlier Tl numbers were measured.

| Row | Tl (repo bench, `KindBench`) | Tl (job barrier each step) | BL model (blended clip) | BL model (unblended clip) | Ratio BL/Tl (vs barrier) | Ratio BL/Tl (vs repo bench) |
|---|---|---|---|---|---|---|
| A: all playing | 8.7 | 9.1 | 66.8 | 53.6 | 7.3 | 7.7 |
| B: 10% playing | 2.0 | 2.4 | 29.5 | 28.4 | 12.3 | 15.1 |
| B0: none playing | 0.55 | 1.0 | 20.5 | 19.8 | 20 | 37 |
| C: staggered loop start | 8.4 | 9.2 | 65.6 | not run | 7.1 | 7.9 |
| J: 12-step `jump` window (3 clips, staggered) | not run | 13.6 | 113.9 | 93.6 | 8.4 | n/a |
| E: empty world, us per step | 6.2 | 9.9 | 29.3 | not run | 3.0 | 4.7 |
| GC allocation per warm step | 0 B | not read | 0 B | 0 B | | |

Notes on the columns:

- "repo bench" is `KindGame.KindBench.RunBatch` unchanged. It does not wait
  for jobs at the end of each step. "Job barrier each step" is the same
  worlds with `CompleteAllTrackedJobs` after every step, which is what the
  model does. The barrier column is the fair one for the ratio; the two Tl
  columns bracket it. The repo bench A row (8.6 and 8.7) matches the barrier
  row closely, so little work is hiding past the timer.
- Spread between the two launches: Tl A 8.6 to 9.5, BL A 65.7 to 67.9, Tl B0
  0.95 to 1.05, BL B0 19.7 to 21.2, Tl E 8.7 to 11.0 us, BL E 28.5 to 30.0 us.
- Row J runs the `jump` timeline (arc/lift, walk and stat clips) with start
  offsets `e % 12` and times 12 steps from the start, so each step has about
  8% of entities finishing. It includes the first step. The BL model gives
  the same three clips (12, 12 and 1 ticks) with an auto-stop director.
- Model checksum: the sampled owners' `Motion` sums matched the expected
  value in every row (420000 for the 210-step rows), so the model computes
  the right result.

### Without Burst safety checks (one launch, second round)

Burst safety checks cost differently in the two designs. One extra launch
with `BurstCompiler.Options.EnableBurstSafetyChecks = false`:

| Row | Tl (repo bench) | Tl (barrier) | BL model | Ratio vs barrier |
|---|---|---|---|---|
| A | 4.2 | 4.8 | 46.6 | 9.7 |
| B | 1.4 | 1.9 | 21.9 | 11.5 |
| B0 | 0.5 | 1.13 | 11.7 | 10.4 |
| C | 4.5 | 5.7 | 49.0 | 8.6 |
| J | n/a | 8.8 | 82.9 | 9.4 |
| E, us per step | 5.7 | 13.7 | 27.6 | 2.0 |

This is a single launch, so treat it as indicative. The empty-world Tl row is
noisy here (5.7 against 13.7 us).

### What the numbers mean in absolute terms

At 100k owners with safety checks on, the BL model costs about 6.7 ms per
step when all play and about 2 ms when none play, against about 0.9 and 0.1
ms for Tl. Real games rarely run 100k timeline owners. Scaled linearly (an
extrapolation, not measured), 1,000 playing owners cost about 67 us in the
model and about 9 us in Tl. Both are small. The difference matters for crowds,
large numbers of idle timeline instances, or a tight frame budget.

## How to redo the model measurement

The model code is not in this repository on purpose. A scratch Unity project
(newest Unity 7 editor, Entities 7.0.0) with one editor-only assembly is
enough. The harness runs through `-batchmode -nographics -executeMethod`.

Workload (same shape as `KindBench`): 100,000 owner entities, each with its
own timeline instance, 10 warm-up steps, 200 measured steps, one step equals
one tick (time 1). Rows A, B, B0, C and E start 100%, 10%, 0%, 100% staggered
by `e % 40`, and 0 owners. The timeline loops over 0 to 40 with one walk clip
0 to 40, speed 2, that adds its value to the owner's `Motion.X` each step.
Row J uses an auto-stop director of 12 ticks with walk 0 to 12, lift 0 to 12
and stat 0 to 1 clips, started at `e % 12`.

Entities per owner: owner, director, and one (track, clip) pair per track.
Components modelled:

- Director: `Timer`, `TimerRange` (loop or auto-stop), `ClockSettings` (constant
  clock, delta 1), `ClockData`, `TimerActivation`, `TimerLifecycle`,
  `TimerEvaluation`, `TimerPaused`, `TimelineActive`, `TimelineActivePrevious`
  (the three enableable), and a `TimerDataLink` buffer with inline capacity 0
  that holds the track and clip entities.
- Track: `TimelineActive`, `TimelineActivePrevious`, `TrackBinding`,
  `TimerData`.
- Clip: `Clip`, `TimerData`, `TimeTransform`, `ActiveRange`, `LocalTime`,
  `ClipActive`, `ClipActivePrevious`, `TimelineActive`, `TimelineActivePrevious`,
  `TrackBinding`, `DirectorRoot`, an animated float component and, for the
  "blended" variant, `ClipWeight` (value 1).

Systems and jobs modelled, in order, all Burst compiled, `ISystem` with
`IJobEntity` and `IJobChunk`, all `ScheduleParallel` unless noted:

1. Clock update over active directors.
2. Timer update: `IJobEntity` over active directors (initialise, advance,
   loop or auto-stop, write `TimerEvaluation`, copy time to linked entities
   with `ComponentLookup` writes); `IJobChunk` over every inactive director
   (reset paused, stopped lifecycle, clear evaluation, disable linked
   `TimelineActive` through a lookup).
3. Clip local time: `IJobEntity` over clips with `TimelineActive` enabled and
   a change filter on `TimerData`; second job writes `ClipActive = false` on
   clips whose timeline is disabled.
4. One system per track type (walk, lift, stat), each with: an evaluate chunk
   job writing the animated float; a single-threaded job that clears the
   `NativeParallelHashMap<Entity, MixData>` and raises its capacity to the
   count from `CalculateEntityCountWithoutFiltering`; a parallel chunk job
   that `TryAdd`s unblended active clips; a single-threaded chunk job that
   accumulates weighted clips (up to four weights per target); a size job and
   a parallel-for-defer job scanning the map buckets and adding the blended
   value to the owner's `Motion` through a `ComponentLookup`.
5. Enable-bit copy of `ClipActive` to `ClipActivePrevious` and of
   `TimelineActive` to `TimelineActivePrevious`.

Timing: `Stopwatch` around the group update plus `CompleteAllTrackedJobs`,
per step, so every job finishes inside the timed region.

## The limits of this comparison

Read these before using any ratio.

1. **It is a model of their design, not their package.** I read their source
   and rebuilt the shape of their systems. The real package may be faster or
   slower than the model. It would need to be about 7 times faster than my
   model to match Tl at row A, which I think is unlikely given the work it
   has to do, but I have not measured it.
2. **Simplifications that make the model slower than the real package:**
   enable-bit copies are done entity by entity because the public API has no
   bit-copy call (their `Core` extension probably copies the mask; going from
   systems 6 to 7 in the model adds up to about 4 ns when idle and is within
   noise when playing); the weighted job does a lookup then an indexer write
   instead of a single `GetOrAddRef`; times are `long` instead of
   `DiscreteTime`.
3. **Simplifications that make the model faster than the real package:**
   no crossing system (only needed with authored cues), no clip
   extrapolation, no clip weight curves, no nested or composite timers, no
   editor systems, simple blend arithmetic, one clip per track, and the
   clip function writes a constant rather than reading game data.
4. **The Tl workload is richer than the model's.** Tl's `Walk.Evaluate`
   reads `WalkStat`, `Target` and an anchor lookup, and the `Tick` handles
   requests, boundaries and pulses. The model's clip function is trivial.
   Adding the same real evaluation to both would shrink the ratio.
5. **Editor batch mode.** Numbers include Burst safety checks (table 1) and
   are not player-build numbers. A second table without safety checks gives
   ratios of 8.6 to 11.5 for A, B, B0, C and J, and 2.0 for the empty
   world.
6. **Sample size.** Two Editor launches, round two of each, plus one launch
   without safety checks. Spreads are listed under the table. Differences
   under about 10% between rows are noise.
7. **Memory is not measured.** The entity counts differ (four per one-clip
   owner against one for Tl), but I did not measure bytes or chunk
   occupancy.
8. **Features are compared from documentation and source reading.** I did not
   run BL's sample scene, its tests, or the Tl Timeline bridge.
9. The BL snapshot I read is a single commit (`9f0de90`, 2026-10-26); I
   could not see its history.

## When to pick which

Pick the BovineLabs Timeline when:

- Designers need to author in the Unity Timeline window with live preview
  of the DOTS state, nested timelines, authored cross-fades between clips,
  clip extrapolation, or authored cue ordering.
- The number of simultaneously live timeline instances is in the hundreds or
  low thousands, so the per-instance plumbing cost is small next to your
  frame budget.
- You already use the BovineLabs package set and its license (LGPL-3) fits.

Pick Tl kinds when:

- You have many timeline owners (crowds, projectiles, status effects) and
  the per-step cost and idle cost of the timeline layer matter.
- You want game code and clips to feed the same per-kind loop (damage
  pulses from physics next to damage pulses from a clip).
- You need the same library to run in a .NET host and in Unity with equal
  results, zero allocation per warm step, and a deterministic merge order.
- You can accept authoring from json or the Timeline bridge, and a young
  package.

Neither is a drop-in replacement for the other: the data models differ (an
entity per clip against a buffer per kind), so a switch means re-authoring
timelines and rewriting the track systems.
