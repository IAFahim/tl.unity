# tl for Unity

`com.iafahim.tl` is the data-authored timeline runtime package for Unity ECS and
Burst: an asmdef runtime layer over `Unity.Entities`, EditMode/PlayMode oracle
tests, the `TlBakeShim` fixture baker shim, and the headless Unity project
receipts under `tests/`.

Extracted from [IAFahim/tl](https://github.com/IAFahim/tl) at commit `3e67333`;
the git history of `unity/`, `tests/Tl.UnityAnalyzerReceipt`,
`tests/Tl.Unity.DataAuthored`, and `tests/Tl.Unity.Project` is preserved and
grafted here.

License: to be decided by the repository owner.

## Snapshot notes

- `com.iafahim.tl/Runtime/Data.cs`, `Hooks.cs`, and `Playback.cs` were symlinks
  into `tl/src/Tl.Core` at the extraction commit; they are snapshot copies whose
  source of truth is `tl/src/Tl.Core` at commit `3e67333`.
- The R13 unmanaged seam in `Data.cs` uses `delegate* unmanaged[Cdecl]`
  explicit calling conventions because Unity's Mono compile target rejects the
  default unmanaged convention (CS8889). `tl/src/Tl.Core` at `bd3c3ca` still
  carries the convention-less form; keep both compilable when porting.
- `Data.cs` also carries an explicit-table `Timeline.TickUnmanaged` overload so
  the Bursted coordinator passes `UnmanagedTable.Block` as job data instead of
  referencing the static block from compiled code.
- The oracle fixture domain (`AlphaTrack` and siblings in
  `com.iafahim.tl/Tests/Fixtures.cs`) lives in the bare namespace `Fixtures`
  because tl schema v1 authoring rejects dotted namespaces; baked pair keys
  hash the assembly-qualified names of that namespace on both the bake and
  runtime sides.
- `tools/TlBakeShim/TlBakeShim.csproj` and
  `tests/Tl.UnityAnalyzerReceipt/Tl.UnityAnalyzerReceipt.csproj` reference the
  snapshot copies under `external/tl/` (Tl.Core, Tl.Compiler, Tl.Gen.CSharp at
  tl commit `bd3c3ca`, refreshed to `323c85a`).

## Eligibility and diagnostic policy (Unity adapter)

The contract doc requires an explicit eligibility/diagnostic policy before
enableable components, runtime component removal, and custom query filters are
supported. This is that policy for the Unity adapter; the tl workstream lifts
it into the contract doc. The candidate row set is every entity carrying
`TimelineComponent`; mandatory columns are the components registered through
`TimelineEcs.Column<T>()` / `TimelineEcs.EnableableColumn<T>()`.

1. Presence (mandatory column removed). Column presence is chunk-uniform: a
   chunk whose archetype lacks a registered mandatory column yields no
   pointer, and every row in it is excluded for the tick with reason
   `MissingMandatoryColumn`. The coordinator's row query names only
   `TimelineComponent`, so removed columns leave the row visible to the
   coordinator instead of silently vanishing from an all-in-one query.
2. Enabled masks (`IEnableableComponent`). For enableable mandatory columns
   the host reads each row's enabled bit on the main thread before any job is
   scheduled. A row whose mandatory column is present but disabled is excluded
   with reason `DisabledMandatoryColumn`.
3. Exclusion is a total no-op. An excluded row executes no consumer and
   commits no movement: its column values, `Position`, and `Cycle` are
   untouched for that tick. Other eligible rows still execute.
4. Eligibility is resolved before scheduling, host-side. Excluded rows never
   enter the job's row list: the host emits one entry per maximal contiguous
   eligible run, and the Burst job body is unchanged code over that list. No
   eligibility logic exists inside the job.
5. No concealment. Every exclusion is recorded, with entity, chunk, row index,
   and reason, into the preallocated diagnostic sink `TimelineEligibility`
   (fixed capacity; exhaustion throws rather than truncating). A no-match
   iteration is therefore always explainable: the consumer record sink shows
   what executed and the exclusion sink shows why the rest did not. Nothing
   is dropped silently.
6. Allocation. Eligibility resolution runs on the main thread using unmanaged
   temporaries plus the preallocated sinks; the warm tick path adds no managed
   allocation. Exclusions are cleared at the start of each coordinator update
   and re-evaluated every tick.

Receipts: `com.iafahim.tl/Tests/EligibilityTests.cs` proves 1–5 on both the
managed and Burst coordinator arms (disabled masks, runtime removal,
observable no-match, run splitting inside a shared chunk).
