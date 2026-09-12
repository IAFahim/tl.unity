# WORK-REPORT — R16: self-containment + Bursted timeline coordinator

**Status: COMPLETE** (Phases 0, 1, and 2)

Repository: `/home/i/GitHub/tl.unity` (standalone local repo; deliverable = local commits on `main`).
Session: 2026-09-12, continued a prior agent's partially complete atom. No pushes, no GitHub writes, no LICENSE changes.

## Final commit chain

| Commit | Subject | Result |
|---|---|---|
| `9c97fc2` | Snapshot tl compiler sources and repoint standalone projects | Phase 0 (prior agent) |
| `a1573d3` | Compile unmanaged seam under Mono and register EditMode test assembly | Phase 0 (prior agent) |
| `84a9098` | Add Bursted timeline coordinator with managed parity oracle | Phase 1 (this session) |
| `77dbd55` | Re-bake fixtures with schema v1 tlbake | Phase 2 (this session) |

Working tree after `77dbd55`: clean except `.tl-loop/heartbeat` (loop protocol file, never committed stale).

## Phase 0 — self-containment (reconstructed; executed by the prior agent before it died)

Evidence reconstructed from the two committed checkpoints; the prior agent left no report.

1. `9c97fc2` added `external/tl/` snapshot copies of tl@`bd3c3ca` sources
   (`Tl.Core/{Compiled,Data,Hooks,Kernels,Playback}.cs` + csproj,
   `Tl.Compiler/*.cs` + csproj, plus their committed `bin/`+`obj/` build
   outputs and `external/tl/Directory.Build.props`) and re-pointed the two
   standalone projects away from tl's worktree:
   - `tools/TlBakeShim/TlBakeShim.csproj` →
     `ProjectReference ../../external/tl/Tl.Core/Tl.Core.csproj`
   - `tests/Tl.UnityAnalyzerReceipt/Tl.UnityAnalyzerReceipt.csproj` →
     `ProjectReference ..\..\external\tl\Tl.Core\Tl.Core.csproj`
2. `a1573d3` made the materialized `com.iafahim.tl/Runtime/Data.cs` compile
   under Unity's Mono target (three `[Cdecl]` hunks, listed below), registered
   the EditMode test assembly via
   `com.iafahim.tl/Tests/Tl.Runtime.Unity.Tests.asmdef`, updated README
   snapshot notes, and recorded a green 5/5 EditMode run
   (test-results.editmode.xml, 2026-09-12 20:17:49Z).

Unity version used (matches inherited receipts): **6000.7.0a5 (a15235a53881)**
at `/home/i/Unity/Hub/Editor/6000.7.0a5/Editor/Unity`. No version mismatch; no BLOCKED condition.

## Phase 1 — Bursted coordinator (this session)

### Inherited state (probed, not reverted)

- Uncommitted: `Data.cs` +3 lines (explicit-table `TickUnmanaged` overload),
  modified `test-results.editmode.xml` showing **10 tests: 9 passed, 1 failed**
  (run at 20:37:46Z; failure = `BurstAndManagedArmsReportInformalTimings`).
- Untracked: `com.iafahim.tl/Runtime/TimelineBurst.cs` (+meta),
  `com.iafahim.tl/Tests/BurstOracleTests.cs` (+meta).

### What was wrong and what I changed

1. **Timings test failure.** The managed arm records into the fixed-capacity
   `TimelineConsumers` sink (Capacity 256). Over the 2000-tick loop it
   exhausted capacity: the R14 runtime caught the consumer exception, logged
   it, and stopped each tick, so the parity check compared 256 (managed) vs
   2000 (burst). Fix: both arms now accumulate per-tick record counts and
   reset their sinks each tick; capacity can never be hit and totals compare
   2000 vs 2000. File: `com.iafahim.tl/Tests/BurstOracleTests.cs`.
2. **No-Burst fallback receipt.** Added
   `BurstCoordinatorJobBodyRemainsCorrectWithoutBurst` to
   `BurstOracleTests.cs`: materializes chunk rows/health column pointers
   through public `EntityManager` handles, materializes keys once via
   `TimelineEcs.ComponentKey<Health>()`, and drives the exact per-chunk
   dispatch the Burst job body performs (`Tl.Timeline.TickUnmanaged` over
   caller-owned `UnmanagedTickState`, raw `void**` bases, `ulong*` keys)
   fully managed — forward 100→102 then mirrored −3 back to 0 — asserting
   full record parity with the managed coordinator arm, position 0, and the
   330f health column total.
   - First attempt used `InternalsVisibleTo("Tl.Runtime.Unity.Tests")` +
     direct `TimelineUnmanagedTickJob.Execute()`. **Rejected:** IVT exposes
     `Profile.cs`'s internal `System.Runtime.CompilerServices.MethodImplAttribute`/`MethodImplOptions`
     polyfills to the test assembly, where Unity's generated
     `AssemblyMonoScriptTypes.generated.cs` then resolves them against both
     `Tl.Runtime.Unity` and `mscorlib` → CS0433 compile failure. IVT was
     removed entirely (`AssemblyInfo.cs` deleted) and `TimelineBurst.cs` was
     restored to the inherited job-inline form. This IVT incompatibility is a
     durable property of this package (mscorlib-shadowing polyfills in
     `Profile.cs`); future agents should not re-add IVT from `Tl.Runtime.Unity`.
   - Second attempt fixed two API mismatches:
     `EntityManager.GetComponentTypeHandle<T>(bool isReadOnly)` requires the
     explicit argument, and the extension `GetUnsafeBufferPointerWithoutChecks`
     does not resolve in this Burst/Collections version — used
     `NativeArrayUnsafeUtility.GetUnsafePtr` / `.GetUnsafeBufferPointerWithoutChecks`
     instead.

### Production files (Phase 1)

- `com.iafahim.tl/Runtime/TimelineBurst.cs` (+`.meta`, inherited content,
  committed as-is): `TimelineBurst.CreateGroup(World)` registering
  `[DisableAutoCreation] [UpdateInGroup(typeof(TimelineSystemGroup))]`
  `TimelineUnmanagedCoordinatorSystem` (ISystem); keys filled once per domain
  (`TimelineEcs.FillKeys`, sealed on first update), per-chunk column base
  pointers materialized on the main thread into `NativeArray<IntPtr>` with
  `ColumnLimit` stride, per-chunk row pointers into `TimelineChunkRows`, and a
  single `[BurstCompile] IJob` (`TimelineUnmanagedTickJob`) running all chunks
  through the R13 unmanaged seam (`Tl.Timeline.TickUnmanaged`).
- `com.iafahim.tl/Runtime/Data.cs` — the only runtime divergence from the tl
  snapshot. tl 3e67333 and bd3c3ca `src/Tl.Core/Data.cs` are **byte-identical**
  (verified via `git archive` + `diff -q`), so all hunks below are against
  tl@bd3c3ca; committed in `a1573d3` (+) and `84a9098` (+):
  1. `@@ -304,7` `PairRuntime.ConsumeUnmanaged`: both `delegate* unmanaged`
     parameters → `delegate* unmanaged[Cdecl]<...>` (Mono CS8889).
  2. `@@ -315,6` **added** public overload:
     `public static unsafe void TickUnmanaged(UnmanagedTickState* state, byte* table, TimelineComponent* rows, int rowCount, void** bases, ulong* keys, int keyCount, uint gameTick, int delta)`
     → `UnmanagedTick.TickCoreUnmanaged(state, table, ...)` — lets the Burst
     job receive `UnmanagedTable.Block` as job data instead of referencing the
     static block from compiled code.
  3. `@@ -654,7` `UnmanagedTable.Consumer`: `Execute`/`Bind` fields →
     `unmanaged[Cdecl]`.
  4. `@@ -666,7` `UnmanagedTable.Install`: both parameters → `unmanaged[Cdecl]`.
- `README.md`: snapshot notes document hunks 2 and (Phase 2) the fixture
  namespace move.

### Phase 1 receipts

`tests/Tl.Unity.DataAuthored/test-results.editmode.xml` (run started
2026-09-12 21:00:56Z): **11/11 passed, `UNITY_EXIT=0`**.
`BurstOracleTests` 6/6: forward/backward parity vs the managed arm,
authored-order + mirrored consumers on fixture_b/c, non-throwing prefix
contract on fixture_d (burst arm records all three steps, managed arm stops at
the throwing consumer), unmanaged seam under a managed caller, timings parity,
no-Burst fallback. `OracleTests` 5/5 unchanged.

Informal timing (correctness-first, not a benchmark):
`tl burst timings: managed=12ms burst=4ms ticks=2000 records=2000` (Burst
2.0.0 IL post-processor active in the run).

Committed as `84a9098`.

## Phase 2 — schema v1 re-bake (this session)

- Built the baker **outside tl worktrees**:
  `git -C /home/i/GitHub/tl archive bd3c3ca | tar -x -C /tmp/tl-archive-unity`,
  then `dotnet build /tmp/tl-archive-unity/tools/Tl.Bake/Tl.Bake.csproj -c
  Release -o /tmp/tlbake-bin` (exit 0). Rebuilt the shim:
  `dotnet build tools/TlBakeShim/TlBakeShim.csproj -c Release` (exit 0), which
  refreshes `external/tl/Tl.Core` bin/obj (committed in `77dbd55`; the only
  content change there is `restoreAuditProperties.enableAudit false→true`
  plus rebuilt equal-size dll/pdb artifacts).
- Schema v1 (from `TimelineBaker.cs` + `docs/data-authored-api.md` at
  bd3c3ca): root `name?/duration/loop/tracks[]`; track and clip each carry
  `name?/namespace/type/assembly?/data?` (type identity never inherited);
  clips add `start`/`end`; grouping derived at bake; `trackType`, `track`,
  `payload`, `clipType` are rejected diagnostics; **`namespace` must be a bare
  single-segment name (dotted = diagnostic)**.
- Conversion attempt 1 failed: `"namespace": "Tl.Unity.Tests"` rejected
  (`dotted name rejected`). Because baked pair keys are
  `Keying.Of(typeof(T).AssemblyQualifiedName)` (`TypeKey<T>`), the fixture
  domain must be v1-authorable **and** identical on bake and runtime sides, so
  the domain moved wholesale to the bare namespace `Fixtures`:
  - `com.iafahim.tl/Tests/Fixtures.cs`: `namespace Tl.Unity.Tests;` →
    `namespace Fixtures;`
  - `com.iafahim.tl/Tests/{Domain,OracleTests,BurstOracleTests}.cs`: added
    `using Fixtures;`
  - `OracleTests.BakedPairKeysMatchUnityRuntimeKeys`: the hard-coded
    provenance string updated to
    `Fixtures.AlphaTrack, Tl.Runtime.Unity.Tests, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null`
  - five `tools/TlBakeShim/fixtures/*.json`: rewritten to v1 with
    `"namespace": "Fixtures"`, `"assembly": "Tl.Runtime.Unity.Tests"` on every
    element, `payload`→`data`, `trackType` split into `namespace`+`type`.
- Bake (all exit 0), then metadata-strip so committed bytes stay pure core
  TLB1 like the previous bake:
  `/tmp/tlbake-bin/Tl.Bake tools/TlBakeShim/fixtures/<f>.json /tmp/tlbake-out/<f>.tlb --assembly tools/TlBakeShim/bin/Release/net10.0/Tl.Runtime.Unity.Tests.dll`
  then `/tmp/tlbake-bin/Tl.Bake --strip /tmp/tlbake-out/<f>.tlb /tmp/tlbake-out/<f>.bytes`,
  copied into `tests/Tl.Unity.DataAuthored/Assets/Resources/TlFixtures/`.
- Byte receipts: sizes unchanged (a=192, b=240, b2=240, c=224, d=224). Every
  byte is identical to the previous bake **except the 8-byte `NativePair.Key`**
  per pair (e.g. fixture_a offsets 48–55), which re-hashes
  `Fixtures.*` assembly-qualified names consistently on both sides.
- Suite after re-bake: first run **10/11** (exit 2) — only the provenance
  string assert above failed (it still named `Tl.Unity.Tests.AlphaTrack`);
  after the one-string fix: **11/11 passed, `UNITY_EXIT=0`**. Committed as
  `77dbd55`.

## Command log (this session, exact results)

| Command | Result |
|---|---|
| `git status` / `git diff` / `git log` (probe) | inherited state as described above |
| Unity EditMode run (inherited XML) | 10 total, 9 passed, 1 failed (timings capacity) |
| Unity run 2 (`-logFile /tmp/tl-unity-editmode-run2.log`) | exit 1 — CS0433 MethodImplAttribute/MethodImplOptions in `Tl.Runtime.Unity` vs mscorlib (IVT attempt) |
| Unity run 3 (`/tmp/tl-unity-editmode-run3.log`) | exit 1 — CS7036 `GetComponentTypeHandle`, CS1061 `GetUnsafeBufferPointerWithoutChecks` |
| Unity run 4 (`/tmp/tl-unity-editmode-run4.log`) | **exit 0 — 11/11 passed** (Phase 1 receipts) |
| `dotnet build tools/TlBakeShim/TlBakeShim.csproj -c Release` | 0 errors |
| `dotnet build /tmp/tl-archive-unity/tools/Tl.Bake/Tl.Bake.csproj -c Release -o /tmp/tlbake-bin` | 0 errors |
| 5× `Tl.Bake <json> <tlb> --assembly <shim dll>` (attempt 1) | exit 1 — `dotted name rejected: 'namespace' ... 'Tl.Unity.Tests'` |
| 5× bake + strip (attempt 2, `Fixtures` namespace) | all exit 0; sizes 192/240/240/224/224; only pair-key bytes differ |
| Unity run 5 (`/tmp/tl-unity-editmode-run5.log`) | exit 2 — 10/11, `BakedPairKeysMatchUnityRuntimeKeys` provenance string |
| Unity run 6 (`/tmp/tl-unity-editmode-run6.log`) | **exit 0 — 11/11 passed** (Phase 2 receipts) |
| `git commit` ×2 | `84a9098`, `77dbd55` |

All Unity runs used:
`/home/i/Unity/Hub/Editor/6000.7.0a5/Editor/Unity -batchmode -runTests
-projectPath /home/i/GitHub/tl.unity/tests/Tl.Unity.DataAuthored
-testResults /home/i/GitHub/tl.unity/tests/Tl.Unity.DataAuthored/test-results.editmode.xml
-testPlatform EditMode -logFile <log>`.

## Deviations and notes for the integrator

1. **Fixture domain namespace is now `Fixtures`** (was `Tl.Unity.Tests`). This
   is forced by schema v1's bare-namespace law and keeps
   `TypeKey`/`PairRuntime` keys identical on bake and runtime sides. All four
   touched test files and the five JSONs are in `77dbd55`; no other file in
   the repo references the old names (grep-verified).
2. **Do not add `InternalsVisibleTo` from `Tl.Runtime.Unity`** to any
   assembly compiled by Unity: `Profile.cs`'s internal
   `MethodImplAttribute`/`MethodImplOptions`/`Interlocked`/`NativeMemory`
   polyfills will produce CS0433 against mscorlib in consumer assemblies'
   Unity-generated MonoScript files.
3. Phase 0 receipts were reconstructed from commits (prior agent left no
   report); everything else in this report was executed and observed in this
   session. Unity 6000.7.0a5 satisfied the version requirement; Phase 0 never
   blocked.
4. The Burst timings line is an informal in-process measurement inside the
   EditMode run (12ms vs 4ms over 2000 ping-pong ticks) and is not a
   BenchmarkDotNet receipt; per repo rules it must not be quoted as a
   performance result.
5. Nothing was pushed; the four commits above are the complete deliverable.
   `/tmp/tl-archive-unity` and `/tmp/tlbake-bin` are disposable scratch; the
   bake is reproducible from the recorded commands.
