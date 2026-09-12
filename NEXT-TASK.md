# NEXT-TASK — implementer brief for the tl.unity repository (R16)

**STATUS: OPEN — this task is waiting for an implementer.**

Work ONLY in `/home/i/GitHub/tl.unity` (standalone LOCAL repository extracted from IAFahim/tl at 3e67333; no remote; local commits ARE the deliverable). NEVER touch `/home/i/GitHub/tl-56-feat-data-authored-api`, other tl worktrees, or /home/i/GitHub/tl.c. The `unity` CLI is installed on this machine.

## Loop protocol (binding)
- Read `/home/i/GitHub/tl.unity/README.md` first, then this file. HEARTBEAT: overwrite `.tl-loop/heartbeat` (ISO timestamp + phase) every ~10 minutes. TIME-BOX: at T+90 minutes write `WORK-REPORT.md` (`Status: PARTIAL` if needed) and exit; BLOCKED beats idling. Write real files within 10 minutes.

# Task: self-containment + Bursted timeline coordinator

## Phase 0 — make the repository self-contained and green (do this first)
Known caveats (README): `tools/TlBakeShim` references tl's `src/Tl.Core`; `tests/Tl.UnityAnalyzerReceipt` references tl's `src/Tl.Core` + `src/Tl.Gen.CSharp`. Fix by pointing at THIS repo's own materialized copies or adding minimal snapshot copies (record every file you copy or re-point; `com.iafahim.tl/Runtime/{Data,Hooks,Playback}.cs` are already materialized snapshots of tl/src/Tl.Core at 3e67333 — that snapshot includes the R13 unmanaged seam and R14 kernel lane). Then run the EditMode test suite via the unity CLI (headless, like the receipts this repo inherited) and record `test-results.xml` + exit code. If the Unity version needed differs from what is installed, report BLOCKED with the exact version math.

## Phase 1 — Bursted coordinator (the actual R16 goal)
A Burst-qualified `[BurstCompile]` job/system that ticks timelines through the R13 unmanaged seam: caller-owned `UnmanagedTickState`, `Timeline.TickUnmanaged`-shaped dispatch (the materialized Data.cs contains it), component columns passed as raw pointers, keys materialized once per asset on the main thread. Receipts (EditMode): parity with the managed arm forward AND backward (authored order + mirrored consumers), looping wrap, exception-free non-throwing contract, and a no-Burst fallback that is still correct (Burst disabled path). Record timings informally; receipts are correctness-first.

## Phase 2 — schema v1 re-bake (only if Phases 0-1 are green early)
The fixture JSONs under `tools/TlBakeShim/fixtures/` predate tl's schema v1 (flat: per-element namespace/type/assembly?/data; clips carry start/end; derived grouping). Convert them and re-bake the committed `TlFixtures/*.bytes` using a tlbake built from tl@bd3c3ca — build it OUTSIDE tl's worktrees: `git -C /home/i/GitHub/tl archive bd3c3ca | tar -x -C /tmp/tl-archive && dotnet build /tmp/tl-archive/tools/Tl.Bake -c Release -o /tmp/tlbake-bin`. Baked fixtures must keep the EditMode suite green.

## Constraints
Local commits on tl.unity's main branch (small, green checkpoints). No pushes, no GitHub, no LICENSE changes. No explanatory comments in production code. WORK-REPORT.md at exit: phases completed, files copied/re-pointed, receipts, every command + exact result, deviations.
