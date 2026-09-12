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
- `tools/TlBakeShim/TlBakeShim.csproj` still references
  `../../../src/Tl.Core/Tl.Core.csproj`, and
  `tests/Tl.UnityAnalyzerReceipt/Tl.UnityAnalyzerReceipt.csproj` still references
  `src/Tl.Core` and `src/Tl.Gen.CSharp` in the source repository. Those projects
  do not build standalone here until the references are re-pointed.
