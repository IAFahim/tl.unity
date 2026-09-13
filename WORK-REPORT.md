# WORK-REPORT — R17b: finish gate-5 (split-run receipt, IJobEntity parity + delayed jobs)

**Status: PARTIAL** — Part 1 (split-run receipt) is complete and green as commit
`a1bc696`. Part 2 (IJobEntity parity + delayed jobs) is a compiling harness whose
three receipts are still red; it is committed as a clearly labeled WIP checkpoint
`56ba086` with the exact resume point. The time-box was exceeded during the last
red/green cycles, so per loop protocol this report is filed now.

Repository: `/home/i/GitHub/tl.unity` (standalone local repo; deliverable = local
commits on `main`). Session 2026-09-13, ~05:05–07:20 +06:00. No pushes, no network
calls, nothing written outside this repository.

## Commit chain

| Commit | Subject | Result |
|---|---|---|
| `a1bc696` | Fix split-run column bases and prove the receipt un-skipped | Part 1 GREEN: 19 passed / 0 failed / 0 skipped, exit 0, `error CS`=0 |
| `56ba086` | Checkpoint: Phase 2 IJobEntity harness compiles, receipts still red | Part 2 WIP: 19 passed / 3 failed, exit 2; production runtime code unchanged from `a1bc696` |
| *(final)* | Record R17b work report | this file |

## Part 1 — `DisabledRowInsideSharedChunkSplitsEligibleRuns` (COMPLETE, GREEN)

Un-skipped the test in `com.iafahim.tl/Tests/EligibilityTests.cs` and fixed two
real host-side bugs; policy and `Data.cs` untouched.

1. **Base-list stride mismatch.** Both run consumers indexed the shared column
   base list with stride `TimelineEcs.ColumnLimit` (4) while `Emit` appends
   `columnCount` (1 here) entries per run. Managed arm read `bases[4]` of a
   2-entry list (indexer throw after 2 records); Burst arm read an unchecked
   out-of-bounds pointer. Fix: managed coordinator strides by `columnCount`;
   `TimelineUnmanagedTickJob` strides by `KeyCount` (identical value).
2. **Column bases not offset per run.** Ticks pass span-relative row indices to
   consumers while the consumers index chunk-base pointers, so a run starting at
   chunk row N executed its consumers against rows `[0..count)` of the chunk
   (observed after fix 1: the excluded-middle test wrote the first row's Health
   twice — 330 vs 165 — on both arms). Fix: `Emit` now offsets each run's column
   base by `startRow * elementSize`; `ColumnEntry` carries the element size
   captured at registration via `sizeof(T)`.

Receipts (`tests/Tl.Unity.DataAuthored/test-results.editmode.xml`, run at
2026-09-12T23:20:41Z): `result=Passed total=19 passed=19 failed=0 skipped=0`,
`UNITY_EXIT=0`, `error CS` count in log = 0. The split-run receipt passes on BOTH
the managed and Burst coordinator arms; all six BurstOracle and five OracleTests
remain green.

## Part 2 — IJobEntity parity + delayed jobs (WIP, exact resume point inside)

Followed `tests/Tl.Unity.DataAuthored/WIP/README.txt`: copied the harness into
`com.iafahim.tl/Tests/`, then red/greened through Unity runs 3–8 (logs
`/tmp/tl-unity-editmode-r17b-run3..8.log`). The R17 "generator skips the job"
issue turned out to be three stacked, independently reproducible causes:

1. **File-scoped namespaces break the JobEntityGenerator's injection.** Its
   `GetBaseDeclaration` only walks `SyntaxKind.NamespaceDeclaration`; with
   `namespace X;` the injected partial lands in the global namespace and the job
   never gains `IJobChunk`/`__TypeHandle` (CS0426/CS0315/CS1061 at the generated
   Schedule extensions). Block-scoping the namespace fixed injection.
2. **`Tl.Unity` shadows unqualified `Unity.Entities` inside generated code**
   (CS0234 "'Entities' does not exist in 'Tl.Unity'"): the generator emits
   unqualified `Unity.*` references, and enclosing namespace `Tl` contains our
   runtime namespace `Tl.Unity`. Hosting the IJobEntity in top-level namespace
   `TlUnityJobs` fixed resolution.
3. **Editor 6000.7.0a5 binary/source drift:** the prebuilt `Unity.Entities`
   binary lacks `ComponentSystemBase.OnCreateForCompiler()` while the shipped
   SystemGenerator emits `protected override` for SystemBase systems with
   rewritten bodies (CS0115). Durable consequence: any SystemBase here must stay
   free of SystemAPI-rewritten constructs. `TimelineObserveSystem` uses an
   IJobChunk observe job and passes.

The harness in `com.iafahim.tl/Tests/IJobEntityParityTests.cs` now compiles
(`error CS`=0 in run 8) and all 19 pre-existing tests stay green. Receipt 1/2
scheduling deviates from the brief: the IJobEntity struct is genuine and its
injection succeeds, but the coordinator schedules a hand-written
generator-equivalent runner (`TimelineIJobEntityTickJobRunner : IJobChunk`,
masked Execute, per-row enableable skip, calls the IJobEntity `Execute` per row)
over a query derived from the Execute signature, because the generated
query-less Schedule path could not be proven within the box.

Remaining reds and next actions (all in one Unity run):

- `IJobEntityArmMatchesManagedArmForwardAndBackward` +
  `IJobEntityDerivedQueryAppliesEligibilityPolicy`: the runner arm records 0.
  The identical runner recorded 2 and committed correctly inside
  `DelayedSelectionDependencyBlocksCommitUntilCompletion` in the same run —
  bisect the fixture delta: parity/policy fixtures also create the managed
  group (`TimelineEcs.CreateGroup`) in the same world and schedule with
  `selection = state.Dependency` (default), while delayed schedules with a
  non-default combined dependency and no managed group.
- `DelayedSelectionDependencyBlocksCommitUntilCompletion`: the test body passes
  completely (no early commit, exactly-once commit, pre-tick observed value,
  chained observe read) but TearDown fails: the dependency manager still holds
  the gate job's `HealthHandle` registration at world dispose. Keep the
  `ScheduleGate` JobHandle and `Complete()` it explicitly after release, or
  route the gate through `_observe.Dependency`.
- Then: delete `tests/Tl.Unity.DataAuthored/WIP/`, commit the Phase 2 atom.

## Validation performed

- EditMode runs only (per-task scope). Unity 6000.7.0a5 at
  `/home/i/Unity/Hub/Editor/6000.7.0a5/Editor/Unity`, invocation:
  `-batchmode -runTests -projectPath …/tests/Tl.Unity.DataAuthored -testResults
  …/test-results.editmode.xml -testPlatform EditMode -logFile <log>`; every log
  grepped for `error CS` (0 in runs 2, 7, 8; each earlier red run diagnosed).
- Production diff vs `a1bc696` (Part 2): empty. `Data.cs` divergence remains
  exactly the four documented hunks; no `InternalsVisibleTo` added; consumer
  chains still install once per process; job bodies remain eligibility-free.
- Deviation note: `NEXT-TASK.md` carries the coordinator's own R17b rewrite as
  an uncommitted change (pre-existing at session start); left uncommitted.
