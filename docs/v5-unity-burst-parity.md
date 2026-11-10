# v5 Unity Burst parity receipt (Burst World)

Run date: 2026-11-10. Unity editor: 7000.0.0a16 (only installed editor
`unity editors --installed --json`; no other Unity 7 editor on this
machine). Backend: Editor CoreCLR, clr 10.0.x (per NUnit XML).

## What landed (NEW files only; no vendored `Core/` changes, no bake-format
changes, `Editor/KindReportMenu.cs` untouched)

- `com.tl.unity/Runtime/TlBurstWorld.cs` (+ Unity-generated `.meta`):
  `Tl.Unity.BurstWorld` — Burst-compiled World equivalent of
  `Tl.World`. Plus `Tl.Unity.ParallelApplyBurst` — the Burst-job
  equivalent of tl's `ParallelApply` (`Threads.cs`, not vendored in
  `Core/`): `ChunkCount`/`ChunkSize`/`Partition` mirror
  `ParallelApply.ForEachChunk` contiguity semantics (contiguous chunks,
  count = workers clamped to [1, total], remainder spread over leading
  chunks). Used as the `IJobParallelFor` batch size.
- `com.tl.unity/Tests/BurstWorldParityTests.cs` (+ `.meta`): 16 tests —
  7 shape hashes vs .NET receipt + scalar oracle + sequential `World`,
  thread invariance (1/2/4/8 = oracle, on uniform/mixed/four-pairs),
  chunk-partition coverage (4 worker counts × 4 totals), invalid-start
  counting.

## Job design (bit-exact by construction)

`BurstWorld` extracts the library tables once into `NativeArray`s
(durations, step windows/kinds, lane blobs copied from the pinned
library) and builds per-row visit plans in the exact final visit order
of `World.Tick` (run order by `(timeline, phase)`; multi-kind stable
order by `(kindOrder, kindId, timeline, step)` when kinds differ).
Each `Run` schedules two `[BurstCompile(FloatMode = Strict)]`
`IJobParallelFor`s:

1. `RunValuesJob` over run descriptors: for each run, frame and step,
   the lane value (pure evaluation, no accumulation order anywhere).
   Writes a disjoint `[run, frame, step]` slice (`[NativeDisableContainerSafetyRestriction]`
   on that field — required because a read-write array in
   `IJobParallelFor` is otherwise restricted to the job index element).
2. `RowAccumulateJob` over rows with `ParallelApplyBurst.ChunkSize`
   as batch size: each row sums its plan serially — frame order, then
   plan order — i.e. the identical addition sequence to the sequential
   `_acc` loop for that row. Rows are disjoint, so the hash is
   independent of thread count by construction; any reordered
   run-major partial-reduce is deliberately not used (it would
   reorder additions and is not bit-exact in general).

## Hash table (.NET vs Burst, FNV-1a over SingleToInt32Bits, 97 ticks)

| Case | .NET (tl receipt) | Unity Burst (7000.0.0a16) | Match |
|---|---|---|---|
| UniformForwardMatchesOracle (const 40x6f, 1024 pairs, phase 0) | `5A7C42B6B1067325` | `5A7C42B6B1067325` | yes |
| UniformRewindMatchesOracle | `5A7C42B6B1067325` | `5A7C42B6B1067325` | yes |
| MixedForwardMatchesOracle (varying lane 40, phase `(i*37+11)%40`) | `19EFEF6313C65325` | `19EFEF6313C65325` | yes |
| MixedRewindMatchesOracle | `B9EF75341D971325` | `B9EF75341D971325` | yes |
| UniqueForwardMatchesOracle (varying lane 1024, phase `i%1024`) | `D77E142E80A83325` | `D77E142E80A83325` | yes |
| UniqueRewindMatchesOracle | `2627DB56B4AC7325` | `2627DB56B4AC7325` | yes |
| FourPairsForwardMatchesOracle (4 timelines 40 ticks, const 6/7/8/9, 4096 pairs) | `6EE2EE4A060F3325` | `6EE2EE4A060F3325` | yes |
| SixtyFourCohortsTwoActive (64 timelines, 1024 rows x 2 active) | (no .NET hash oracle; tl uses a ratio pin) | `5413647772067325` (Burst = scalar oracle = sequential `World`, 1-thread = 8-thread) | yes (see deviation) |

Thread invariance (each worker count printed as
`TL-BURST case=<name> workers=<n> hash=<X16>`): uniform, mixed and
four-pairs run at workers 1/2/4/8 all print the oracle hash.
64-cohorts: workers1 = workers8 = `5413647772067325`.

## Batch runs (one reused scratch project, `file:` manifest entry,
lock `_work/unity.lock` held during runs, absolute
`-testResults`/`-logFile`, no `-quit` with `-runTests`)

- EditMode: exit 0, `error CS` count 0, XML `total=66 passed=65
  failed=0 skipped=1`. Baseline before this change: `total=50
  passed=49 skipped=1` (pre-existing `KindBakingTests` `Assert.Ignore`
  when `TL_TLB` unset). The 16 new `BurstWorldParityTests` all passed.
- PlayMode: exit 0, `error CS` count 0, XML `total=30 passed=30
  failed=0 skipped=0` (identical to the pre-change baseline of 30/30).
- Scratch project lock released; batch logs kept under
  `C:/Users/Nibir/_work/batch/` (not committed). Nothing was committed
  (held for Main review).

## Deviations from the tl algorithm (with justification)

1. **Parallel axis is rows with per-row serial accumulation, not a
   run-major parallel accumulate.** tl's `ParallelApply.ParallelAccumulate`
   sums `acc[rows[i]] += values[i]` in parallel slices, which is only
   hash-stable when each row appears once. Here each run writes its
   own lane values (parallel, disjoint) and rows accumulate in the same
   per-row addition order as the sequential loop. A run-major partial
   sum + reduce would reorder additions (frame-major vs run-major) and
   would not be bit-exact for inexact float sums, so it is not used.
2. **Lane evaluation is inlined in the values job** (`plane[tick]` /
   constant), because the jobs cannot touch the pinned managed
   `Library` pointer. Byte-identical lane values (the scalar fallback
   bodies `Kernels` compiles under netstandard2.1, same as the
   sequential vendored path).
3. **`ParallelApplyBurst` lives in the `Tl.Unity` runtime assembly, not
   in `Core/`.** `Core/` is the hand snapshot consumed by
   `eng/sync-tl-core.sh --check`, so new non-vendored files cannot go
   there without breaking the sync gate. `Threads.cs` remains
   unvendored, matching the existing sync set.
4. **64-cohorts-2-active has no .NET hash pin in tl** (the tl fact is a
   timing ratio). This cut pins it Burst = scalar oracle = sequential
   `World` = `5413647772067325`, plus 1-thread = 8-thread invariance,
   and records the hash above. The timing ratio is not re-pinned
   (cross-machine flaky, not part of the hash contract).

## Known limits of this cut

- Visit plans are built host-side (managed) when the playback set
  changes; the hot `Run` is two Burst jobs over preallocated
  `NativeArray`s. Warm `Run` allocates only the per-call values table
  (`Allocator.TempJob`, disposed after completion); the zero-alloc
  warm-tick guarantee is a property of sequential `World`, not claimed
  here.
- `BurstWorld` supports `Start` + `Tick`/`Run` + frame/active/invalid
  reporting. `Stop` is not exposed (no parity test needs it; add when
  a caller does).
- The `LibraryBlob` NativeArray path from `tl/docs/v5-unity.md` is
  unchanged: this world still builds from a managed `Library` at load
  and then never touches its pin.
