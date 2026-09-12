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
  tl commit `bd3c3ca`).
