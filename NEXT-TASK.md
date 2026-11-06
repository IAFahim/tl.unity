# NEXT-TASK — implementer brief for the tl.unity repository (R17d — close gate-5 Part 2)

**STATUS: OPEN — this task is waiting for an implementer.**

Work ONLY in `/home/i/GitHub/tl.unity` (standalone LOCAL repository; no remote; local commits ARE the deliverable). NEVER write to other repos/worktrees. Unity CLI: the newest installed Unity 7 editor (`python3 eng/unity_editor.py editor`), batchmode EditMode (results to `tests/Tl.Unity.DataAuthored/test-results.editmode.xml`; ALWAYS grep the log for `error CS`; NEVER pass `-quit` together with `-runTests`).

## Loop protocol (binding)
- Read `/home/i/GitHub/tl.unity/README.md`, the R17c report (`git show 45679a1` body or `WORK-REPORT.md`), and `tests/Tl.Unity.DataAuthored/WIP/R17C-NOTES.txt` first, then this file. HEARTBEAT: `.tl-loop/heartbeat` (ISO timestamp + phase) every ~10 minutes. TIME-BOX: T+75 minutes — commit what is green, write `WORK-REPORT.md`, exit; PARTIAL beats idling.
- One commit per green milestone with receipts in the message body.
- Production `com.iafahim.tl/Runtime/**` stays FROZEN (zero diff); `Data.cs` divergence stays the four hunks; all prior durable laws (IVT ban, once-per-process install, TimelineConsumers-before-Seal, block-scoped namespaces for IJobEntity, no SystemAPI-rewritten SystemBase constructs).

# Task: land the three Part-2 receipts (22/22 green) and delete WIP/

The integrator has pre-made the Fix-3 decision: **drop `[BurstCompile]` from the test IJobEntity** (`TlUnityJobs.TimelineIJobEntityTickJob`), keep the genuine generated query/Schedule path running on Mono, and document the deviation in the test file header (BC1091: Burst cannot compile the static-table `TickUnmanaged` — `Tl.UnmanagedTable`'s static constructor calls `NativeMemory.AlignedAlloc`; the table is internal and the IVT ban forbids capturing it test-side; Burst parity of the tick path is already receipted by the direct Burst coordinator arm). Do NOT attempt to make the test job Burst-compile.

## Step 1 — one diagnostic run (Fix 1 bisect)

Apply the diagnostic patch from `WIP/R17C-NOTES.txt` section 2 (runner `Diag` counters + the 4-variant world-shape matrix: managed-group on/off × dummy `IJob` gate on/off), run EditMode ONCE (invocation in notes section 4), and read the interpretation table (notes section 3): chunks=0 → query cache problem; rows skipped → enableable mask; rows>0/sink=0 → consumer chain. This same run validates Fix 2 (delayed test body AND TearDown). Record the matrix outcome in the report.

## Step 2 — implement Fix 1 from the diagnosis

Per the diagnosed cause. Known suspects from R17b/R17c: the managed group's `TimelineCoordinatorSystem.OnCreate` binding/sealing interplay, and default `state.Dependency` scheduling vs the delayed arm's non-default combined dependency. Keep the fix in the TEST assembly unless the diagnosis proves a Runtime defect — in that case STOP, write the evidence in WORK-REPORT.md as BLOCKED-on-integrator, and do not edit Runtime.

## Step 3 — Fix 3 + finish

Apply the pre-made Fix-3 decision. Then: delete `tests/Tl.Unity.DataAuthored/WIP/` entirely, run the full suite green — **22 passed / 0 failed / 0 skipped, exit 0, `error CS`=0** — commit with receipts, write `WORK-REPORT.md` as the final commit (status, the bisect matrix outcome, Fix-1 root cause, receipts, the documented Fix-3 deviation, files changed).
