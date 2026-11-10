# v5 Unity BurstWorld vs 4.0 timeline-job path — A/B receipt

Run date: 2026-11-10. Unity editor: 7000.0.0a16. Backend: Editor CoreCLR,
clr 10.0.10 (per NUnit XML). Machine: AMD Ryzen 5 8500G, 12 logical
(6 cores), `Environment.ProcessorCount=12`, `JobsUtility.JobWorkerCount=11`,
Burst enabled True. Owner item 4: the tl.unity 4.0-era job path
(`TimelineKernel` / `KindTick` / `TimelineJobs` / `TimelineSystems` generated
jobs) against `TlBurstWorld` on the PhaseClock LargeBench hostile shapes.

## What landed

- `com.tl.unity/Tests/BurstWorldVsTimelineJobsTests.cs` (+ `.meta`):
  measure-only A/B, 2 cases (`Large1MBy1_BurstVsTimelineJobs`,
  `Large4MBy4_BurstVsTimelineJobs`). Every arm is asserted bit-identical
  against its scalar .NET oracle; the test fails on any hash divergence.
  Timings are logged as `TL-AB TIME` lines (min of rounds); no timing
  thresholds (cross-machine flaky).
- This doc (`docs/v5-unity-burst-world-vs-timelinejobs.md`).

## Method

Shapes (PhaseClock LargeBench hostile, tl `benchmarks/PhaseClock/LargeBench.cs`):
duration 1024, VaryingLane `lane[i] = 1f + (i*7%13)*0.5f + (i%5)*0.25f`,
phase `(e*37+11)%1024`, row `(e*97)%N`, full step window `[0,1024)`, loop,
97 oracle ticks, single step per timeline. Grouped by row (entity):
1M-1 = 1_000_000 rows x 1 playback = 1_000_000 visits/tick, 1 timeline.
4M-4 = 4_000_000 rows x 4 playbacks = 16_000_000 visits/tick, timelines 1..4
(`timeline = (e/rows)+1`, so each row sees one playback per timeline;
ScaleBench `perRow` semantics, LargeBench `e` formulas preserved).

Arms:

- **BurstWorld** (`Tl.Unity.BurstWorld`, `TlBurstWorld.cs`): one
  `LibraryWriter+Verify` library (one full-window loop step per timeline,
  VaryingLane) + `Start` per playback + `Run(Forward, 97)` for the hash,
  then `Tick` loops for timing. Workers: `Tick(workers 1)` vs
  `Tick(workers 0 = ProcessorCount)`.
- **4.0 KindTick** (`KindTickJob`, `TimelineKindTickSystem` body): one ECS
  world, one entity per row with `Playback` + `TimelineRequest` +
  `TimelinePlaying` (enabled). Playback slots set directly
  (Timeline/Tick=phase, Channel 0, Flags None; producer `Start` clamping +
  StartPending bypassed). Kindless `KindIndex` (KindCount 0) so
  `TickEntity`'s fast path steps Tick with no events — the 4.0 entrypoint
  that steps Tick per row. `Schedule` (1 worker) vs `ScheduleParallel`
  (N = JobWorkerCount).
- **4.0 Advance** (`TimelineAdvanceJob`, `TimelineJobs.ScheduleAdvance`
  body): ECS world with `Playback` only (production non-kind path),
  same direct slot setup, same `Schedule` / `ScheduleParallel` timing.
  See the `AdvancePlayback` finding below.

Measurement: warmup + min-of-rounds (1M-1: warmup 30, rounds 5, 60 ticks/
round; 4M-4: warmup 5, rounds 3, 10 ticks/round, ScaleBench BenchParams).
ns/visit = min-round seconds * 1e9 / ticksPerRound / visits.

## Measured table (ms/tick min-of-rounds; ns/visit)

| shape | arm | workers | ms/tick | ns/visit |
|---|---|---|---|---|
| 1M-1 | burst | 1 | 9.4076 | 9.408 |
| 1M-1 | burst | N (12) | 2.1542 | 2.154 |
| 1M-1 | 4.0-advance | 1 | 23.9002 | 23.900 |
| 1M-1 | 4.0-advance | N (JobWorker 11) | 3.4546 | 3.455 |
| 1M-1 | 4.0-kindtick | 1 | 31.2423 | 31.242 |
| 1M-1 | 4.0-kindtick | N | 4.2882 | 4.288 |
| 4M-4 | burst | 1 | 93.0932 | 5.818 |
| 4M-4 | burst | N (12) | 19.7236 | 1.233 |
| 4M-4 | 4.0-advance | 1 | 217.5847 | 13.599 |
| 4M-4 | 4.0-advance | N | 34.4906 | 2.156 |
| 4M-4 | 4.0-kindtick | 1 | 137.1773 | 8.574 |
| 4M-4 | 4.0-kindtick | N | 19.3162 | 1.207 |

Ratios `burst / 4.0` (TL-AB RATIO lines):

| shape | burst/kindtick @1 | burst/kindtick @N | burst/advance @1 | burst/advance @N |
|---|---|---|---|---|
| 1M-1 | 0.30x | 0.50x | 0.39x | 0.62x |
| 4M-4 | 0.68x | 1.02x | 0.43x | 0.57x |

Reading: at 1M-1 BurstWorld is ~2x faster than the 4.0 kind-tick fold at N
(2.15 vs 4.29 ms/tick) and ~3.3x at 1 worker (9.41 vs 31.24). At 4M-4 the
two are at parity at N (19.72 vs 19.32 ms/tick, 1.02x) while BurstWorld is
still faster at 1 worker (93.09 vs 137.18, 0.68x). Advance is slowest on
both shapes (its ratio is reported, not hidden — see finding).

## Hashes (bit-identical, asserted; any divergence fails the bench)

| shape | oracle hash | arms |
|---|---|---|
| 1M-1 | burst `0962314E5F0A3025` | BurstWorld accumulator (FNV-1a over SingleToInt32Bits, frames 1..97) == scalar oracle |
| 1M-1 | kindtick `E58C0EFA6F375165` | final Tick == (phase+97)%1024 (FNV-1a over tick uints) == scalar oracle |
| 1M-1 | advance `4DEAA47F2C2F04E5` | final Tick == phase (only 1023 wraps) == scalar oracle |
| 4M-4 | burst `8E5DA0D8E772B725` | BurstWorld accumulator == scalar oracle |
| 4M-4 | kindtick `515069FEDEF6BF25` | final Tick == (phase+97)%1024 == scalar oracle |
| 4M-4 | advance `A63C9BB79DC6D6D4` | final Tick == phase (only 1023 wraps) == scalar oracle |

Cross-arm tick agreement: BurstWorld's folded tick for frame f is
`(phase+f)%1024`; its after-97 accumulator closes over the same tick
sequence KindTick steps to `(phase+97)%1024`. Both hashes are exact
(FNV-1a 64, no float tolerance).

## Setup costs (visible in balance)

- Burst library bake (LibraryWriter+Verify per shape): 1 ms (1M-1,
  1 timeline), 0 ms (4M-4, 4 timelines) — negligible.
- BurstWorld collect (`Start` per playback) + first `Run(97)`:
  1M-1: collect 19 ms, first Run 450 ms (includes BurstWorld plan rebuild
  + first Burst dispatch). 4M-4: collect 345 ms, first Run 12_935 ms
  (16M playbacks, plan rebuild + 97-frame Run).
- 4.0 ECS setup (world create + bulk archetype create + Playback fill +
  query): 1M-1 advance 999 ms, kindtick 1_018 ms; 4M-4 advance 3_797 ms,
  kindtick 3_659 ms. 4.0 library view setup is `Marshal.AllocHGlobal`
  + fill (unlogged, sub-millisecond).
- 4.0 has no O(N^2) setup analog (direct slot fill, no FindPlain);
  BurstWorld has no bake-format cost beyond the lane blob copy at ctor.

## Batch runs (reused probe `file:` project, absolute results/logs,
no `-quit` with `-runTests`)

- EditMode: exit 0, `error CS` count 0, XML `total=69 passed=68 failed=0
  skipped=1`. The 2 new A/B cases passed (hash asserts above). The skip is
  pre-existing (`KindBakingTests` `Assert.Ignore` when `TL_TLB` unset).
  Totals include the on-disk `WorldV5ParityTests` (14 cases) and the two new
  A/B cases on top of the prior 53 (`53 + 14 + 2 = 69`).
- PlayMode: exit 0, `error CS` count 0, XML `total=30 passed=30 failed=0
  skipped=0` (identical to baseline).
- Logs: `C:/Users/Nibir/_work/batch/unityab-EditMode.xml/.log`,
  `unityab-PlayMode.xml/.log` (not committed).

## Findings / deviations

1. **`TimelineKernel.AdvancePlayback` does not step normal forward ticks.**
   Core `TimelineWorld.Advance` (`TimelineWorld.Systems.cs`) ends with
   `else current->Tick++`; the Unity `AdvancePlayback` has no normal-step
   increment (non-wrap forward slots never change Tick). The Advance arm
   therefore only wraps Tick==1023 to 0, modeled in its oracle, and its
   slower timings (chunk dispatch for a near-noop) are reported as-is.
   KindTick's `TickEntity` is the 4.0 entrypoint that steps Tick per row
   (the named per-row/per-kind fold), so the headline comparison uses
   KindTick. No Runtime/Core edit was made for this finding (would change
   `PlaybackRulesTests` expectations and the sync gate).
2. **4M-4 timeline assignment.** LargeBench is single-timeline; for the
   ×4 shape the per-playback timeline is `(e/rows)+1` so each row sees one
   playback per timeline 1..4 (ScaleBench perRow semantics, LargeBench
   `e` formulas preserved). The 1M-1 shape is exactly LargeBench
   (1 timeline, `row=(e*97)%N`).
3. **Worker mapping.** BurstWorld N = `Environment.ProcessorCount` (12);
   ECS N = `ScheduleParallel` across `JobWorkerCount` (11). 1 worker =
   single `IJobChunk.Schedule` / `Tick(workers 1)`. No thread knob is
   forced via `JobHandle.SchedulerMode`.
