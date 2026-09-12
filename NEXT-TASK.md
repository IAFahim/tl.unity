# NEXT-TASK — implementer brief for the tl.unity repository (R17)

**STATUS: OPEN — this task is waiting for an implementer.**

Work ONLY in `/home/i/GitHub/tl.unity` (standalone LOCAL repository; no remote; local commits ARE the deliverable). NEVER touch `/home/i/GitHub/tl-56-feat-data-authored-api`, other tl worktrees, or `/home/i/GitHub/tl.c`. The `unity` CLI is installed (6000.7.0a5 at `/home/i/Unity/Hub/Editor/6000.7.0a5/Editor/Unity`).

## Loop protocol (binding)
- Read `/home/i/GitHub/tl.unity/README.md` and the R16 report at the bottom of `git log -1 740b7df` (or `WORK-REPORT.md` if still present) first, then this file. HEARTBEAT: overwrite `.tl-loop/heartbeat` (ISO timestamp + phase) every ~10 minutes. TIME-BOX: at T+90 minutes commit what is green, write `WORK-REPORT.md` (`Status: PARTIAL` if needed) as a final commit, and exit; BLOCKED beats idling. Write real files within 10 minutes.
- Local commits are allowed HERE (this repo has no remote; the integrator reviews `git log`). Keep each phase a separate commit with its receipts in the message body.
- DURABLE LAW from R16: do NOT add `InternalsVisibleTo` from `Tl.Runtime.Unity` — `Profile.cs` mscorlib-shadowing polyfills cause CS0433 in Unity-generated MonoScript files of consumer assemblies.

# Task: eligibility/diagnostic policy + remaining gate-5 receipts (enabled masks, missing components, IJobEntity parity, delayed jobs)

Spec source: `docs/data-authored-api.md` in the tl repo, paragraph "An asset with no consumers for a pair still has valid timing…" (read it via `git -C /home/i/GitHub/tl-56-feat-data-authored-api show 323c85a:docs/data-authored-api.md` — do NOT edit anything in that worktree) and gate item 5: "Prove Unity direct-system and IJobEntity parity, enabled masks and missing components, with delayed jobs showing no early overwrite or commit."

## Phase 0 — refresh the tl snapshot (small, do first)

`external/tl/` is pinned at `bd3c3ca`. Refresh `external/tl/Tl.Core/*.cs` (+ Tl.Compiler if changed) to `323c85a` via `git -C /home/i/GitHub/tl-56-feat-data-authored-api show 323c85a:src/Tl.Core/<file>` per file (read-only against that repo; write only inside tl.unity). Expected diff: `Kernels.cs` gains the phase-2 seam (`Chain` scratch parameter + AggressiveInlining; emitter-side chunking does not affect this repo). Re-apply the four documented R16 divergence hunks to `com.iafahim.tl/Runtime/Data.cs` if any touched region shifted (the hunks: three `[Cdecl]` conversions + the explicit-table `TickUnmanaged` overload — see README snapshot notes). Rebuild TlBakeShim, run the EditMode suite, require 11/11 exit 0. Commit.

## Phase 1 — eligibility policy (define + prove)

The contract requires an EXPLICIT eligibility/diagnostic policy before the implementation is accepted. Implement and receipt this policy in the Unity adapter:

1. **Enabled components (masks)**: a row whose `TimelineComponent` exists but whose mandatory consumer component is DISABLED via `IEnableableComponent` at tick time must be excluded BEFORE `Tick`, making that row a total no-op for the tick, while other eligible rows still execute. Receipt: a fixture with 2+ entities where one has the consumer component disabled — the disabled entity's column is untouched, the enabled entity's executes, and the tick completes.
2. **Runtime component removal**: an entity that loses the consumer component between build and tick must NOT partially execute other consumers then commit, and must NOT silently conceal the condition. Receipt: destroy/remove the component, then tick — the coordinator must either exclude the row explicitly (observable: row not executed, no partial writes) or surface a diagnostic before any effects. Whichever behavior the seam makes correct, the receipt must prove "no partial execution + no concealed incomplete row".
3. **Local no-match ≠ concealment**: an iteration that matches nothing locally (all rows excluded) must not conceal incomplete execution of an otherwise selected row — the crossed-frame count and per-row execution decision must stay observable (e.g., the existing record sink shows the row was excluded, not silently dropped).
4. Document the policy in this repo's README (the tl repo's contract doc is owned by the tl workstream — write the Unity-side policy text here; the integrator lifts it into the contract doc).

Constraints: no managed allocation on the tick path; the exclusion check runs on the main thread before scheduling (host-owned mask read); the Burst job body stays unchanged (eligibility is resolved before the job is scheduled — rows excluded by the host never enter the job's chunk row list, or enter with an explicit eligibility bit you define and receipt).

## Phase 2 — IJobEntity parity + delayed jobs

1. **IJobEntity parity**: author a consumer as an `IJobEntity` job (Unity derives the query from `Execute` parameters) alongside the existing direct-system coordinator, and receipt identical per-tick records vs the direct/Burst arms over a forward+backward window on one of the baked fixtures. If the generated `IJobEntity` wrapper needs the timeline dependency chain, follow the contract: "The Unity group must supply the selection dependency to consumers, collect every scheduled consumer handle, and pass the combined dependency to the next stage or commit."
2. **Delayed jobs — no early overwrite or commit**: schedule a consumer job with an artificially delayed `JobHandle` (e.g., a job that spins/awaits several frames worth of ticks in a controlled test) and prove the coordinator does not commit stage output early: while the dependency is incomplete, downstream reads observe the pre-tick values and the commit lands exactly once after completion. Receipt: a test that forces incomplete-then-complete dependencies and asserts no intermediate overwrite and no double commit.
3. Compiler assemblies stay out of players: extend the existing package/asmdef checks if trivial; otherwise note state.

## Report

Final commit `WORK-REPORT.md`: status, policy text, per-phase receipts (exact Unity CLI invocations + exit codes + test counts), the snapshot diff summary, files changed, deviations, and the exact next atom if anything remains. Keep `test-results.editmode.xml` updated in the repo as before.
