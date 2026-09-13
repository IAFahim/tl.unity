# WORK-REPORT — R17d: close gate-5 Part 2 (land IJobEntity/delayed receipts, 22/22, delete WIP/)

**Status: PARTIAL** — Fix 1 is ROOT-CAUSED and the Fix-3 decision is APPLIED, but the
final suite run is blocked by one remaining compile error in the source-generated
IJobEntity code (namespace shadowing, see section 5). The 75-minute box expired during
the fix-validate loop. Production `com.iafahim.tl/Runtime/**` was never modified
(frozen; `Data.cs` divergence intact; no InternalsVisibleTo). No pushes, no network;
nothing written outside `/home/i/GitHub/tl.unity`.

## Commit chain

| Commit | Subject | Result |
|---|---|---|
| `45679a1` | (R17c baseline) Fix-2 applied, Fix-1 open | start state |
| `f6b78d4` | Bisect Fix 1: runner embeds unassigned IJobEntity __TypeHandle | diagnosis + diag patch, receipts |
| *(this commit)* | Apply Fix 3 (genuine IJobEntity Schedule, no BurstCompile), gate cleanup, WIP/ removal | compile-blocked, see section 5 |
| *(this commit)* | Record R17d work report | this file |

## 1. Pre-flight blocker removed

The R17c "killed" diagnostic Unity instance (PID 1266428) was still alive, spinning at
100% CPU for 40+ CPU-minutes on the reverted `_observe.Dependency` deadlock and holding
the project lock; every new Unity launch aborted with "another Unity instance is running
with this project open". Killed PID + import worker, removed stale
`Temp/UnityLockfile`. First relaunch attempt with the diag patch hit
`NativeArray<T>.Clear()` (does not exist in this Collections version) — 3x CS1061, fixed
by zeroing elements.

## 2. Bisect matrix (run 3, one EditMode run as briefed)

Runner `Diag` counters + 4-variant world-shape matrix (managed group x nop gate):

```
variant=0 managedGroup=False gated=False chunks=0 rows=0 skipped=0 sink=0 exclusions=0
variant=1 managedGroup=True  gated=False chunks=0 rows=0 skipped=0 sink=0 exclusions=0
variant=2 managedGroup=False gated=True  chunks=0 rows=0 skipped=0 sink=0 exclusions=0
variant=3 managedGroup=True  gated=True  chunks=0 rows=0 skipped=0 sink=0 exclusions=0
```

All four variants identical: the job body never executed a single chunk in any world
shape — neither the managed group nor the gate/default dependency is the cause. The
notes' interpretation table resolves to "chunks=0 -> query/job-data problem", and the
log names it exactly.

## 3. Fix 1 ROOT CAUSE (proven, with receipts)

`TimelineIJobEntityTickJobRunner` embedded `TimelineIJobEntityTickJob` (IJobEntity) as a
plain field. The Entities source generator augments every IJobEntity struct into an
IJobChunk carrying an `__TypeHandle` field (ComponentTypeHandles) that must be assigned
through the generated `Init`/`Schedule` path. The test constructed the struct directly,
so the embedded `__TypeHandle` was zeroed, and EVERY `Schedule` of the runner threw
inside `JobsUtility.Schedule`:

```
InvalidOperationException: The UNKNOWN_OBJECT_TYPE TimelineIJobEntityTickJobRunner.
JobData.Job.__TypeHandle.__Tl_Unity_TimelineComponent_RW_ComponentTypeHandle has not
been assigned or constructed. All containers must be valid when scheduling a job.
  at JobsUtility.Schedule -> JobChunkExtensions.ScheduleInternal
  at TimelineIJobEntityCoordinatorSystem.OnUpdate (IJobEntityParityTests.cs:154)
```

The ISystem codegen wrapper catches and `Debug.LogException`s it, so tests failed by
clean assertions ("record count parity: expected 4, was 0") with no visible exception —
exactly the R17b/R17c mystery. Run 3 logged it 9 times (4 parity ticks + policy +
delayed + diag variants); run 8 (R17b, `/tmp/tl-unity-editmode-r17b-run8.log`) shows
the same 5 occurrences — the runner NEVER executed anywhere. The delayed arm additionally
failed `CompleteAllTrackedJobs`/dispose against the orphaned untracked gate registration
("job has not been registered with SystemBase.AddDependency"). NOT a Runtime defect —
no Runtime edit needed, no BLOCKED-on-integrator.

## 4. Fix 3 + Fix 1 + delayed-gate fix (applied, one compile error from validation)

- Dropped `[BurstCompile]` from `TimelineIJobEntityTickJob`; documented the BC1091
  deviation in the test file header (pre-made integrator decision).
- Coordinator now schedules the genuine generated path:
  `new TimelineIJobEntityTickJob {...}.Schedule(_derived, selection)` — generated query
  construction, chunk iteration, per-row enableable skip, Schedule; Mono execution.
  Broken runner struct deleted.
- `TimelineGateJob` became a bare `IJob` (writes `Observed[0]=1f`, spins on `Release`),
  no `ComponentTypeHandle<Health>`, scheduled as an untracked fence instead of a
  query-schedule. This removes the orphaned safety registration that made
  `CompleteAllTrackedJobs`/TearDown throw while keeping the receipt's semantics:
  the coordinator still combines the gate into `selection`, nothing commits while
  incomplete, commit lands exactly once after release, and the observe stage still
  proves chained-dependency commit visibility (`_captured[0]==165f`). The dropped
  in-gate Health read (assertion `_observed[0]==0f`) is replaced by `_observed[0]==1f`
  ("gate executed and its fence completed before the tick was observed downstream").
- Delayed Fix-2 (`_gate.Complete()` in the test body) kept as-is.

## 5. EXACT RESUME POINT (one mechanical edit + one run)

Run 4 failed compile with 15x `error CS`, all in the SOURCE-GENERATED IJobEntity code
attributed to `IJobEntityParityTests.cs`:

```
error CS0234: 'Burst' does not exist in the namespace 'Tl.Unity'          (138,17)
error CS0234: 'Entities' does not exist in the namespace 'Tl.Unity'       (141,43/109)
error CS1503: cannot convert 'ref System.Span<Tl.Unity.Entities.ComponentType>'
              to 'ref System.Span<Unity.Entities.ComponentType>'          (142,128/144,166)
```

The generated members reference `Unity.Entities...` WITHOUT `global::`, and the
generator injects them into this file's LAST namespace block, `Tl.Unity.Tests` — where
the enclosing namespace `Tl` contains a member named `Unity` (=`Tl.Unity`), so the
simple name `Unity` binds to `Tl.Unity` and breaks. This is precisely the durable law
"IJobEntity jobs need block-scoped namespaces + non-shadowing top-level namespace".

Resume edit: consolidate the whole file under one non-shadowing block-scoped namespace
(e.g. `namespace TlUnityJobs { ... }` for everything, adjusting references to
`Tl.Unity.Tests` internals like `BurstArms`/`Record`/`Health` — internal/assembly-wide,
so only namespace qualification changes), or move the job struct's file so the generated
code cannot land in a `Tl.Unity.*` scope. Then: one EditMode run — expect 22/22,
`UNITY_EXIT=0`, `error CS=0` — commit with receipts (WIP/ is already deleted in this
checkpoint; its content remains at `0c435e1` if the integrator wants it back until
green).

## Validation performed

- Run 3 (diagnostics): `UNITY_EXIT=2`, 19/23 passed, `error CS`=0, matrix + 9 exception
  receipts captured (section 2-3). The 4 red: parity, policy, delayed (body assert at
  cs:402 + teardown orphaned-gate error), diag fixture (the invalid-job-data exception).
- Run 4 (fix): `UNITY_EXIT` unused (compile failed); 15x `error CS` listed in section 5;
  no test executed. Log: `/tmp/tl-unity-editmode-r17d-run4.log`.
- Production runtime: zero diff (frozen). Unity invocation per notes section 4 (no
  `-quit` with `-runTests`).
