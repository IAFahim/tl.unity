# WORK-REPORT — R17c: finish gate-5 Part 2 (IJobEntity parity + delayed-job receipts)

**Status: PARTIAL** — the delayed-test teardown fix is applied (unvalidated), Fix 1
(parity/policy runner records 0) remains red with a one-run bisect experiment prepared,
and two hard root causes were proven this session: a Burst BC1091 compile failure that
blocks the genuine Burst-compiled IJobEntity path, and a live main-thread/gate deadlock
that forbids the "route the gate through `_observe.Dependency`" teardown fix. The
time-box expired during the diagnostic Unity run, so per loop protocol this report is
filed now; nothing outside `/home/i/GitHub/tl.unity` was written, no pushes, no network.

Repository: `/home/i/GitHub/tl.unity` (standalone local repo; deliverable = local
commits on `main`). Session 2026-09-13, ~06:05–07:20 +06:00, hard 75-minute box.

## Commit chain

| Commit | Subject | Result |
|---|---|---|
| `42fb556` | (R17b baseline) 19/19 green, Part 2 WIP, 3 red | start state |
| *(this session)* | Fix delayed-job gate teardown; checkpoint R17c diagnosis | Fix 2 applied, NOT yet Unity-validated |
| *(this session)* | Record R17c work report | this file |

Production runtime code under `com.iafahim.tl/Runtime/**` was never modified (frozen
per atom; `Data.cs` divergence remains exactly the four documented hunks; no
`InternalsVisibleTo` added).

## 1. Fix 2 — delayed test TearDown (APPLIED, UNVALIDATED)

`DelayedSelectionDependencyBlocksCommitUntilCompletion` body passed at R17b but
TearDown failed: `InvalidOperationException` — the dependency manager still held the
gate job's `HealthHandle` registration at `_world.Dispose()` ("job has not been
registered with SystemBase.AddDependency"). The gate handle returned by
`ScheduleGate` was dropped into the static `TimelineIJobEntityCoordinatorSystem.Gate`,
consumed and reset to `default` inside the coordinator, leaving the registration
orphaned.

Applied fix (the brief's option a, smaller diff): `DelayedDependencyTests` keeps the
handle in a `_gate` field (reset in `SetUp`), assigns it to the static Gate as before,
and calls `_gate.Complete()` immediately after the release +
`CompleteAllTrackedJobs()` in the test body. `ScheduleGate` itself is unchanged.

## 2. Root cause proven: BC1091 blocks the genuine Burst-compiled IJobEntity (Fix 3)

`/tmp/tl-unity-editmode-r17b-run8.log` (lines ~386–408) shows Burst failing at editor
load while compiling the generator-produced `JobChunkProducer<TimelineIJobEntityTickJob>`
entry point:

```
Burst error BC1091: External and internal calls are not allowed inside static
constructors: UnsafeUtility.Malloc(...)
  at NativeMemory.AlignedAlloc (Profile.cs:119)
  at Tl.UnmanagedTable..cctor() (Data.cs:665)
  at Tl.UnmanagedTable.get_Block() (Data.cs:670)
  at Tl.Timeline.TickUnmanaged(...) (Data.cs:317)
  at TlUnityJobs.TimelineIJobEntityTickJob.Execute (IJobEntityParityTests.cs:36)
```

The test IJobEntity calls the static-table `TickUnmanaged` overload; Burst cannot
compile the referenced static constructor, so the `[BurstCompile]` job can never compile
as generated code. This is exactly why the runtime's Burst coordinator
(`TimelineUnmanagedCoordinatorSystem`) captures `UnmanagedTable.Block` on the main
thread and passes the explicit-table overload as job data. `UnmanagedTable` is
`internal` to `Tl.Runtime.Unity`, and the durable law bans `InternalsVisibleTo`, so the
test assembly cannot capture the table pointer; the realistic genuine-path option is
dropping `[BurstCompile]` from the test IJobEntity (generator-emitted query construction,
chunk iteration, per-row enableable skip and Schedule all remain genuine, executed on
Mono — the delayed receipt already proves the static-block path is correct under Mono
from a job). Fix 3 was NOT attempted after Fix 1 stayed open.

## 3. Root cause proven: routing the gate through `_observe.Dependency` deadlocks

The first Fix-2 attempt assigned the gate to `TimelineObserveSystem.Dependency`
(option b). The diagnostic run (`/tmp/tl-unity-r17c-run1.log`, killed at 100% CPU after
~20 min) hard-hung the suite. Mechanism: once the gate is system-owned, the job
participates in the dependency manager's `GetDependency`, so the IJobEntity
coordinator's `state.Dependency.Complete()` at the start of its own update combines the
spinning gate's fence and the main thread waits forever on a job that spins waiting for
the main thread (`TimelineGateJob.Execute` spins on `Release`). At R17b the gate was
unowned, so it never entered `GetDependency` — which is also why the R17b body passed.
Option (a) from section 1 is the correct fix; the attempt was reverted
(`git checkout 56ba086 -- <file>`) before applying (a).

## 4. Fix 1 — parity/policy runner arm records 0 (OPEN, experiment ready)

Facts from run 8 (`test-results.editmode.xml` at `42fb556`):
`IJobEntityArmMatchesManagedArmForwardAndBackward` fails "record count parity: expected
4, was 0" (line 270) and `IJobEntityDerivedQueryAppliesEligibilityPolicy` fails
"expected 2, was 0" (line 292) — sink empty after `CompleteAllTrackedJobs`, no
exceptions, while the identical runner records 2 and commits in the delayed test in the
same run. BurstOracleTests pass in a world that also carries a managed group.

Remaining fixture deltas: (i) parity/policy worlds create the managed group
(`TimelineEcs.CreateGroup`) whose `TimelineCoordinatorSystem.OnCreate` binds the static
`ColumnAccess<T>`/`EnableableColumnAccess<T>` handles and seals `TimelineEcs`; (ii)
parity schedules with `selection = state.Dependency` (default) while the delayed test
schedules behind a non-default combined dependency. Neither is confirmed. A single-run
bisect experiment (runner `Diag` counters for chunks/executed rows/skipped rows, plus a
4-variant world-shape matrix test: managed group on/off x dummy `IJob` gate on/off) is
written out in full in `tests/Tl.Unity.DataAuthored/WIP/R17C-NOTES.txt`, together with
the interpretation table (chunks=0 → query cache; rows skipped → enableable mask;
rows>0/sink=0 → consumer chain).

## Validation performed

- No completed EditMode run this session: the diagnostic run was killed to break the
  deadlock of section 3 (`error CS`=0 in its log up to the kill; the suite had reached
  the BurstOracle block before hanging). The last fully green full-suite receipt remains
  R17b's: 19 passed / 0 failed / 0 skipped, `UNITY_EXIT=0`, `error CS`=0.
- The working tree therefore contains one changed test file
  (`com.iafahim.tl/Tests/IJobEntityParityTests.cs`, Fix 2 only) that has NOT passed a
  Unity run. `tests/Tl.Unity.DataAuthored/WIP/` still exists and must be deleted only
  when the full 22/22 suite is green, per the original brief.

## Exact resume point

1. Apply the diagnostic patch from `WIP/R17C-NOTES.txt` section 2 and run EditMode once
   (invocation in section 4 of the notes; never pass `-quit` with `-runTests`). Read the
   variant matrix, remove the diagnostics, implement the indicated Fix 1.
2. Validate Fix 2 (delayed TearDown) in the same run: it must pass body and TearDown.
3. Fix 3 within a fresh 20-minute box: genuine `Schedule(_derived, selection)` with
   `[BurstCompile]` removed from the test IJobEntity (rationale in section 2); if it
   records and commits, prefer it over the runner and note the Burst deviation in the
   test file header; otherwise keep the runner and record the deviation precisely.
4. Delete `tests/Tl.Unity.DataAuthored/WIP/`; full suite green 22/22, exit 0,
   `error CS`=0; commit with receipts; supersede this report.
