# Tl Unity Playback (`com.tl.unity`)

Tl 4 playback-model runtime for Unity ECS with Burst: playback buffers,
library singleton, simulation systems, and generated system jobs. Compiles
against Tl.Core netstandard2.1.

## Kinds

Game-defined per-step work (value type + combine + apply) fed by clips or
pulsed by clips, markers, and game code. Full model, step order, sizing, and
reports: [docs/KINDS.md](../../docs/KINDS.md).

Samples (`Samples~`, importable from the Package Manager):

- **Kinds IL2CPP player**: Runner on kinds (priority-override boost, summed
  lifts, stat pulses) plus the 600-step script; hash `13943614770702343869`
  equals the .NET run.
- **Bullets IL2CPP player**: game-code Damage pulses + timeline hit pulses in
  one Health write; hash `3081412629212866412` equals the .NET run.

Editor: kind authoring bakes (`TimelinePlayerKindsBaker`, `[RequiresKinds]`,
kind profiles, `KindSets`), `Tools/Tl/Kind Report` (`TLKIND001-003`), and
`Tools/Tl/Bake Kind Capacities (tlb)` (write-only-on-change).
