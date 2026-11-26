# Why the Unity timeline frame is not 10x faster (yet)

A self-contained brief for a reviewer who wants to try. It says what was measured, what the time is spent on, why a further 10x on the whole Unity frame is not reachable by tuning the current design, and which design changes could get closer. Every number names the commit it was measured at and whether a second agent reproduced it.

Repositories: `IAFahim/tl` (the engine, plain .NET) and `IAFahim/tl.unity` (the Unity ECS host, package `com.tl.unity`). Tracking issue: IAFahim/tl#647. Merged work: IAFahim/tl#648 (tl `63d0d16d`), IAFahim/tl.unity#40 (tl.unity `c4d88a6`).

Since IAFahim/tl#652, spans are no longer kind entries: kind jobs read them from a load-time table, and the span-entry cache of sections 5 and 7 (`KindEntryCache`, `KindIndex.TickStamp`, `WindowSteps`) is gone. Sections 3 to 7 record the design as measured at `c4d88a6`; item 4 of section 8 has the new numbers.

## 1. Short answer

- The **timeline step** in Unity is already about 11x faster than before at long timelines (20,000 characters x 200 playbacks, 3,000-tick timelines: 2.84 -> 0.25 ms per frame). That is the part comparable to the .NET result.
- The **whole Unity frame** is 2.2x to 4.5x faster (4.74 -> 2.20 ms at 120-tick timelines, 4.54 -> 1.00 ms at 3,000-tick timelines). It cannot reach 10x with the current design because two costs are not timeline overhead but the work the game asks for:
  1. **Kind events**: every clip start, clip end and loop wrap must add or remove an entry in the character's kind buffers. At 120-tick timelines that is about 100,000 events per frame.
  2. **Per-frame feeder evaluation**: every active span clip runs the user's feeder method every frame (about 500,000 calls per frame in the benchmark), whatever the timeline length.
- The .NET 8x to 164x figures measure a different workload: plain timeline stepping with no kinds and no feeders, on one thread. The .NET kinds path is not lazy yet (stage A2).
- 10x on the whole frame needs at least one of: feeders evaluated only when their inputs change, loop wraps that are not events, or a storage layout that is not one heap block per character per buffer. Each changes semantics, public API or the data layout (section 8).

## 2. Setup

| Item | Value |
| --- | --- |
| Machine | AMD Ryzen 5 8500G (6 cores, 12 threads), DDR5, Linux |
| Unity | 7000.0.0a16, Entities 7.0.0, Burst, Burst safety checks off, job debugger off, leak detection off, 11 job workers |
| .NET | .NET 10, one thread; netstandard2.1 asset measured too (what Unity and .NET 8/9 load) |
| Unity benchmark | `com.tl.unity/Tests/Kinds/Bench/OwnerScaleBench.cs`: 1,000 looping kind timelines, one clip each (half Walk spans feeding `Locomotion`, a quarter Hit pulses, a quarter Stat pulses), random clip start and length up to half the duration; rows x active playbacks 10,000 x 40, 20,000 x 100, 20,000 x 200; durations 120, 600, 3,000 ticks; 64 warmup frames, median of 256 |
| .NET benchmark | tl `benchmarks/Glide`, one process per cell, median of 7 rounds of 256 ticks after 8,192 warmup ticks (`docs/PERF-GLIDE.md`) |
| Unity command | `<Editor>/Unity -batchmode -nographics -quit -projectPath <probe> -executeMethod KindGame.OwnerScaleBench.RunBatch -ownerShapes 20000x200 -ownerDurations 120,3000 -ownerModes eager,lazy -ownerMeasure 256 -logFile <log>` with a probe project whose `Packages/manifest.json` points `com.tl.unity` at the checkout |
| "tick" and "kinds" | `tickMs` is `TimelineKindTickSystem` (its job and completion); `kindMs` is the generated `Movement` and `Vitals` kind systems; `totalMs` is both |

"Eager" is the existing path (every slot stepped every frame). "Lazy" is the opt-in path from IAFahim/tl#647 (a row with `TimelineHorizon`, `TimelineClock` and a `PlaybackDue` buffer). Results are byte-identical between the two in every cell (state hashes equal), and warm frames allocate 0 B.

## 3. Numbers

### 3.1 .NET (tl `63d0d16d`, kind-free `TimelineWorld.Advance`, one thread, ms per tick)

From tl `docs/PERF-GLIDE.md`, reproduced within noise by a second agent from a fresh clone.

| Entities x active | Duration | net10 eager | net10 lazy | Speedup | ns2.1 eager | ns2.1 lazy | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 x 40 | 120 | 0.700 | 0.071 | 9.8x | 0.665 | 0.136 | 4.9x |
| 10,000 x 40 | 3000 | 0.640 | 0.004 | 163.8x | 0.698 | 0.014 | 49.1x |
| 20,000 x 100 | 120 | 3.462 | 0.399 | 8.7x | 3.331 | 1.156 | 2.9x |
| 20,000 x 100 | 3000 | 3.332 | 0.027 | 123.9x | 3.365 | 0.086 | 39.1x |
| 20,000 x 200 | 120 | 6.604 | 0.796 | 8.3x | 6.971 | 2.806 | 2.5x |
| 20,000 x 200 | 3000 | 6.391 | 0.061 | 104.1x | 6.863 | 0.234 | 29.4x |

Memory: lazy tables use 16 to 40 % more private memory (20,000 x 200: 77.6 -> 108.8 MB). The first tick after starting everything at once is 3.5 to 6x slower than an eager first tick (20,000 x 200: 207 against 35 ms).

### 3.2 Unity, whole frame (ms per frame, 11 workers)

At tl.unity `c4d88a6` (merged head), one run, 20,000 x 200:

| Duration | Eager tick | Eager kinds | Eager total | Lazy tick | Lazy kinds | Lazy total | Total speedup | Tick speedup |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 120 | 2.655 | 2.083 | 4.739 | 1.345 | 0.854 | 2.200 | 2.2x | 2.0x |
| 3000 | 2.836 | 1.699 | 4.535 | 0.250 | 0.747 | 0.997 | 4.5x | 11.3x |

Full matrix at tl.unity `8f47456` (one commit before the step cache; the cache took a further 18 to 21 % off lazy kinds), one run:

| Rows x active | Duration | Eager total | Lazy total | Speedup |
| --- | ---: | ---: | ---: | ---: |
| 10,000 x 40 | 120 / 600 / 3000 | 0.443 / 0.392 / 0.456 | 0.304 / 0.195 / 0.157 | 1.5x / 2.0x / 2.9x |
| 20,000 x 100 | 120 / 600 / 3000 | 2.298 / 2.114 / 2.327 | 1.456 / 0.861 / 0.636 | 1.6x / 2.5x / 3.7x |
| 20,000 x 200 | 120 / 600 / 3000 | 4.507 / 4.246 / 4.460 | 2.344 / 1.621 / 1.181 | 1.9x / 2.6x / 3.8x |

A second agent reran the whole matrix at `3e55928` from a fresh clone and a fresh Unity project: every speedup within 8 %.

Starting point (tl.unity `4169cda`, first lazy version): 20,000 x 200 lazy total 3.871 / 2.638 / 1.843 ms at 120 / 600 / 3000.

### 3.3 The same work on both hosts, eager

Unity's eager kind tick at 20,000 x 200 takes 2.6 to 2.8 ms on 11 workers. The .NET eager `Advance` (no kinds) takes 6.4 to 6.6 ms on one thread. Eleven workers buy only about 2.4x: the eager step reads and writes 32 MB of 8-byte slots per frame and is bound by memory bandwidth, not by cores.

## 4. What each host does per frame (where they differ)

| Aspect | tl .NET lazy (`TimelineWorld.Advance`) | tl.unity lazy (`TimelineKindTickSystem` + generated kind systems) |
| --- | --- | --- |
| Workload in the benchmark | Steps playbacks only. No kinds, no feeders. A table driven by generated systems steps eagerly today (`PlaybackTable.View`/`Playing` restore real ticks), so the .NET kinds path is not lazy (A2). | Steps playbacks **and** runs kinds: each clip edge appends or removes kind entries; generated jobs evaluate every active span entry every frame and apply the combined value to components. |
| Threads | One | 11 Burst workers plus the main thread, three jobs per frame (tick, `Movement`, `Vitals`) with dependencies |
| Playback storage | One `PlaybackTable` per world: contiguous arrays, inline capacity 4 per entity, spill region (`src/Tl.Core`) | One `DynamicBuffer<Playback>` per character (16-byte header in the chunk, 100 to 200 slots in a separate heap block) |
| Due bookkeeping | Per-slot `ushort` due tick, per-entity `Next` lower bound; one tick compares `Next` of 64 entities at once, gathers due slots of up to 64 entities, prefetches them, then steps them (`src/Tl.Core/TimelineWorld.Systems.cs`, `LazyTicks.cs`) | Per-slot due tick in a `PlaybackDue` buffer (heap block per character), per-row `TimelineHorizon` byte (ticks until the next due slot) and per-row `TimelineClock`; a due row's dues are scanned with AVX2/SSE2 (`Runtime/TimelineHorizon.cs` `DueScan.Collect`) |
| Tick encoding | Stored tick relative to one table clock | Stored tick relative to the row's clock (`TimelineTicks.Real/Encode/Decode`) |
| Work per due slot | `PlaybackRules.Step`: decode, step, encode, new due | `KindRules.TickSlot` (`Core/KindRules.cs`): decode, move, kind events (append/remove entries, pulses), new due from `KindIndex.Calm`, encode; plus a cache refresh of the row's entries (`KindCursor.Refresh`) |
| Consumers | None in the benchmark | Generated kind jobs (`Tools~/Tl.Gen.Unity/UnityKindJobEmitter.cs`, example `Tests/Kinds/Generated/TlUnity.Movement.g.cs`): per row, per active entry: tick from the entry cache, clip step, frame build, user feeder (`Walk.Evaluate` with a `ComponentLookup<Anchor>`), `Combine`, then `Apply` |
| Change detection | Not needed | Kind jobs trust per-entry caches only when the chunk's `Playback` change version and the index identity match this frame's tick stamp (`KindIndex.TickStamp`) |
| Compiler | RyuJIT (.NET 10 `Vector128`) or netstandard2.1 scalar | Burst (AVX2 in the Editor JIT) |

## 5. Where the Unity frame time goes now

### 5.1 Event and entry counts (20,000 x 200)

- 4,000,000 playbacks. A one-clip looping timeline has about three edges per loop: clip start, clip end, loop wrap (two when the clip ends at the loop point).
- Duration 120: about 100,000 slot events per frame (4,000,000 x 3 / 120). Duration 3000: about 4,000.
- Half the timelines carry a Walk span active about a quarter of the time: about 25 `Locomotion` entries per row, about 500,000 span evaluations per frame, at every duration.

### 5.2 Kind jobs: what one span evaluation costs (decomposition)

Measured at duration 120, 20,000 x 200, lazy, before the slot cache existed, by switching parts of the generated `Locomotion` loop off with a `SharedStatic<int>` selector in one compile:

| Variant | Kind jobs ms | What it removes |
| --- | ---: | --- |
| Full job | 2.22 | |
| No playback-slot read, no tick check | 0.99 | reading one random `Playback` slot per entry: 1.23 ms |
| Also no clip-data read | 0.76 | clip and track data from the library: 0.24 ms |
| Also no user feeder / anchor lookup | 0.53 | `Walk.Evaluate` and its `ComponentLookup`: 0.23 ms |
| Empty entry loop | 0.49 | floor: streaming each row's entry buffers and both kind systems' overhead |

What was done about it (all bit-exact): the slot read was replaced by a 4-byte cache in each span entry (bytes 8 to 11, `KindEntryCache`, refreshed by the tick job), the library step lookup was inlined and then resolved once per clip (window index in bytes 12 to 15, `KindIndex.WindowSteps`). Lazy kind jobs went 2.17 -> 0.85 ms at duration 120. What remains is close to the 0.49 ms floor plus the user's feeder.

### 5.3 Tick job: profile at duration 120

`perf` over the Editor's Burst JIT objects (share of all samples in the measured run, after the slot cache): `TickLazy` 3.5 % (SIMD scan, first touch of due slots, `Calm` lookups for the next due tick), `KindCursor.Refresh` 1.6 %, `KindRules.TickSlot` 1.5 %, `PlaybackRules.Move` 1.0 %, enable bits 0.7 % (since replaced by `EnabledMask`), `RemoveSpan` 0.5 %, `ApplyEvents` 0.5 %, memmove/memset 0.6 %, `AppendEntry` 0.35 %. No single item dominates: the cost is about 100,000 events per frame at roughly 120 to 150 ns each of one thread's time, spread over 11 workers.

Before this work the scan alone was 8 % of samples (a scalar branchy loop over every row's 200 dues); it is now AVX2.

## 6. The arithmetic for 10x

Budget for 10x at 20,000 x 200: eager total / 10 = about 0.47 ms (duration 120) and 0.45 ms (duration 3000).

- **Duration 3000** (lazy 1.00 ms): the tick is 0.25 ms (already 11x its eager cost). The kind jobs are 0.75 ms and do not depend on duration: about 500,000 span evaluations every frame. The empty-loop floor alone (0.49 ms) is over the 10x budget. 10x here requires not evaluating every active span every frame.
- **Duration 120** (lazy 2.20 ms): the tick is 1.35 ms for about 100,000 events. Even at 3x cheaper events (0.45 ms) plus the kind floor (0.49 ms) the frame stays at twice the budget. 10x here requires fewer events (wraps that are not events) **and** change-driven evaluation.
- **Memory**: the lazy path reads 8 MB of dues per frame at duration 120 (every row is due every frame: 200 slots x 3 edges / 120 ticks = 5 events per row per frame), plus one random line per due slot and the entry buffers. Eleven workers share one DDR5 bus; the eager path already shows bandwidth, not cores, as the limit (section 3.3).
- **Fixed costs**: three job schedules and completions per frame, per-chunk setup for 157 chunks of 128 rows. Tens of microseconds; small now, a large share at a 0.45 ms target.

## 7. What was tried

Kept (measured, bit-exact, in `c4d88a6`):

| Change | Where | Effect |
| --- | --- | --- |
| SIMD due scan (AVX2/SSE2, scalar elsewhere) | `Runtime/TimelineHorizon.cs` `DueScan` | lazy tick at d120 1.76 -> 1.16 ms |
| Slot cache in span entries, trusted by change version + index identity | `KindEntryCache`, `KindCursor.Refresh/Forget`, `KindIndex.TickStamp`, emitter | lazy kinds 2.17 -> 1.14 ms |
| `EnabledMask` instead of per-row `SetComponentEnabled` | `KindCursor.SetActive/SetPlaying`, emitter | small (tick 1.36 -> 1.32 ms) |
| Inline `StepInWindow` fast path | `Runtime/KindIndex.cs` | lazy kinds about -10 % |
| Library step resolved once per clip (`WindowSteps` copy, window index in the entry) | `KindIndex.StepOfEntry`, `PackedStep` | lazy kinds -18 to -21 % |

Dropped (measured, below a 3 % kill rule or worse):

| Change | Result |
| --- | --- |
| Prefetching playback slots or entries in the kind jobs | first version -14 %, superseded by the cache; later variants within noise |
| Resolving a row's library steps in a tight first pass | no gain |
| Next-row entry prefetch in generated jobs | 0 to 5 %, both directions |
| Kind entries inline in the chunk (`InternalBufferCapacity` 2 -> 32) | 3 % at d120, 0 at d3000 |
| `[NoAlias]` on generated column pointers | 0 % |
| Entities internals via `.asmref` (bulk enable masks, writes without version bumps; the BovineLabs Core route) | compiles on 7000.0.0a16, bounded at about 0.01 ms here; not adopted (ties the package to Unity internals) |
| BovineLabs Timeline's model (one entity per clip, per-frame clip time, top-4 weighted blend) | estimated tens of ms at this scale and not deterministic in entry order |
| Chunk-level skipping | every chunk has due rows every frame at 100 to 200 playbacks per character |

## 8. Where a 10x might come from (open, ranked by expected gain)

Each item lists what it would change and what it would break. The hard constraints are in section 9.

1. **Change-driven span evaluation.** Re-evaluate a span feeder only when its inputs change: the clip (start/end, blend), the direction, the row columns and lookups it reads. Needs generator analysis of each feeder (which `Frame` members and columns it reads; anything tick-dependent such as `Progress` stays per frame) and change versions on the columns it reads. Potential: kinds 0.75 -> about 0.1 ms at duration 3000, which alone is close to 10x on that frame. Risk: feeders that read through `ComponentLookup` or shared context cannot be proven unchanged cheaply; must stay bit-identical.
2. **Loop wraps that are not events.** Store ticks modulo the timeline duration (`stored = (tick - clock) mod duration`) so a wrap with no kind edge needs no step, no re-encode and no cache refresh. About a third of the events at duration 120. Changes `TimelineTicks.Real` (needs the duration) and every reader.
3. **A slab layout instead of one heap block per character per buffer.** `Playback`, `PlaybackDue` and kind entries as structure-of-arrays slabs per chunk or per world, indexed by row, so a frame streams memory instead of chasing one pointer per row per buffer. Changes the public `DynamicBuffer<Playback>` contract and every reader; the per-row heap hop measured only 3 % on its own once data was cached (section 7), so this pays mainly together with item 1.
4. **Cheaper kind events.** Done in IAFahim/tl#652: spans are no longer entries. A load-time table (`KindIndex.Score`) lists the live spans per (timeline, tick, kind), kind jobs walk playback slots, and `Combine` runs over rated copies first (by source slot, then crossing), then the other slots in slot order, each in pair order (owner-approved re-pin). Owner shape (500 timelines x 16 tracks, 10,000 to 50,000 rows x 40 to 100 playbacks): 3.8x to 4.7x faster, entity heap 21.5 KB to 1 KB per row.
5. **Fusing the tick and the kind jobs per chunk**, so a row's data is still in cache when its feeders run. Breaks the contract that user systems can run between the tick system and the kind systems.
6. **Skip the refresh scan** with a per-row bitmask of slots that own cached entries (about 0.1 ms at duration 120).
7. **A2 on .NET**: a reader for generated systems that does not force eager stepping, so the .NET kinds path gets lazy ticks too.

## 9. Constraints any change must keep

- Results byte-identical to eager stepping: pinned KindScript hashes (`Tests/Kinds/Harness/KindScript.cs`), the lazy differential laws in `Tests/Kinds/Tests/KindHorizonTests.cs` (every tick, through requests, edits, direction flips, limit changes, library republish), host parity with .NET (`Tests/Parity`).
- Deterministic `Combine` order (entry order) on every worker count.
- Unity ECS only: `ISystem`, `IJobChunk`/`IJobEntity`, Burst, unmanaged data; no `SystemBase`, no managed components, no Unity internals without the owner's approval.
- 0 B warm allocation on the main thread; zero new warnings; no code comments (repo rule).
- Lazy rows stay opt-in: turning them on changes the raw `Playback.Tick` value every reader sees.
- Public API changes, pinned-hash changes and source-budget waivers are owner decisions (tl `AGENTS.md`).

## 10. References

Code (paths in `IAFahim/tl.unity` unless marked tl):

- `com.tl.unity/Runtime/KindTick.cs`: `KindTickJob` (`Execute`, `TickLazy`, `Unclock`), `KindCursor` (`AppendEntry`, `Sampled`, `SetActive`)
- `com.tl.unity/Runtime/KindSystems.cs`: `TimelineKindTickSystem` (forget rules)
- `com.tl.unity/Runtime/KindIndex.cs`: `StepInWindow`, `Score`, `SpanCount`, `Calm`
- `com.tl.unity/Runtime/TimelineHorizon.cs`: `DueScan`, `TimelineTicks`
- `com.tl.unity/Tools~/Tl.Gen.Unity/UnityKindJobEmitter.cs`: generated kind jobs
- `com.tl.unity/Core/KindRules.cs` (copy of tl `src/Tl.Core/KindRules.cs`): `TickSlot`, `ApplyEvents`; `com.tl.unity/Core/KindScore.cs`: the span table
- `com.tl.unity/Tests/Kinds/Bench/OwnerScaleBench.cs`: the benchmark
- `docs/KINDS.md`, section "Lazy rows": the contract
- tl `src/Tl.Core/TimelineWorld.Systems.cs`, `src/Tl.Core/LazyTicks.cs`: .NET lazy ticks
- tl `docs/PERF-GLIDE.md`, `benchmarks/Glide`: .NET measurements

History:

- IAFahim/tl#647 (plan and stage comments), IAFahim/tl#648 (A1, .NET), IAFahim/tl.unity#40 (A3, Unity; PR comments carry every measurement round and the second-agent reruns)
- tl.unity commits: `4169cda` (lazy rows), `4fbb739` (SIMD scan, entry cache), `7f88eba` (enabled masks, per-world stamp), `7b806c1` (rows losing `PlaybackDue`), `8f47456` (inline step lookup), `4a69d46` (step resolved once), `c4d88a6` (cache tied to its index, review fixes)
- Method notes (profiling Burst with `perf`, decomposition with a `SharedStatic` switch, change-version trust, what did not help): IAFahim/unity-cli-skill `skills/unity-cli-field-notes/references/burst-performance.md`

## 11. Worker scaling and the slot-walk skips (2026-11-26, Unity 7000.0.0a21)

`eng/worker-scaling.sh` (workflow `worker-scaling.yml`, work branch only) runs `OwnerScaleBench` at 20,000 rows x 200 playbacks with `-job-worker-count` 1, 5 and 11, alternating a base build and the head build in each run. Medians of 256 frames, ms.

Before the change (ba011a0, three runs agree): the kind jobs were 97 % of the frame. Lazy duration 3000 was 22.5 ms total on 1 worker and 7.0 ms on 11; the lazy tick alone was 0.58 ms and 0.21 ms. The tick scales about 80 to 90 % per added worker up to 5 workers and flattens past the 6 physical cores. c4d88a6, where section 3.2 was measured, no longer compiles on a21 (`UnityEngine.TypeManagerV2` is gone), so the 0.75 ms kinds figure there cannot be re-measured.

Where the kind time went: each kind's job walked every playback slot of every row (tick, `CellOf`, two `Score.Start` reads) even for a kind with no spans in the library, and the event walk ran `GetTimeline`, `StageAt` and the stage's steps for every slot even when no step carries a consumer pair. 62015b4 adds `KindIndex.SpanKinds` (kinds with any span) and `KindIndex.PairBloom` (one 64-bit filter of step pair keys per timeline). Generated kind jobs skip the walk for a kind outside `SpanKinds` and test the timeline's kind mask before the tick; event walks skip a timeline whose filter has none of the consumer pairs. Only slots the old walk already ignored are skipped.

A/B against 5a15151 (run 45742181282, two runs, kind ms, base -> head):

| Cell | 1 worker | 5 workers | 11 workers |
| --- | --- | --- | --- |
| eager d=120 | 17.6, 14.8 -> 10.4, 11.9 | 6.6, 12.3 -> 5.1, 6.0 | 7.3, 10.1 -> 5.9, 10.8 |
| lazy d=120 | 18.0, 17.8 -> 10.5, 12.2 | 6.5, 11.9 -> 4.7, 5.6 | 6.3, 9.7 -> 5.0, 14.6 |
| eager d=3000 | 38.2, 30.9 -> 22.8, 32.9 | 10.6, 24.8 -> 10.3, 13.2 | 11.9, 68.8 -> 13.8, 12.7 |
| lazy d=3000 | 44.0, 32.1 -> 28.2, 27.0 | 10.7, 21.5 -> 10.3, 26.8 | 16.0, 41.7 -> 12.2, 6.1 |

Hashes are equal in every cell for both arms (d=120 475C30B6054A7D25, d=3000 73B879360C251716), allocation 0 B, and the EditMode and PlayMode suite passes on 62015b4. On one worker the kinds are about 35 % faster at duration 120 and about 20 % at 3000. Multi-worker cells vary up to 3x between runs of the same build on this runner, so they show no reliable gain or loss; read only the 1-worker column. What is left is the Locomotion feeder itself, evaluated for every live span every frame (section 8 item 1).

