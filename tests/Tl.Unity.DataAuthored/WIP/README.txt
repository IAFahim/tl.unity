R17 checkpoint: Phase 2 harness (IJobEntity parity + delayed dependency receipts).
NOT compiled by Unity: this directory is a sibling of Assets/.

Known compile issues at shelve time (two Unity runs of evidence):
1. ComponentSystem does not exist in Entities 6.7.0/f1c61260f7c9 - TimelineObserveSystem
   must derive from SystemBase (already fixed in this copy).
2. IJobEntity source generator did not emit InternalCompilerQueryAndHandleData for
   TimelineIJobEntityTickJob when the struct was declared `unsafe partial struct`
   (CS0426 at the generated Schedule extension). Fix applied in this copy: move
   `unsafe` to the Execute method. If generation still skips the job, next suspects:
   the [BurstCompile] attribute on an IJobEntity in a non-Burst-tested assembly, or
   the C# using-alias type (UnityRow) in the Execute signature - try spelling
   Tl.Unity.TimelineComponent directly.

Resume: copy this file to com.iafahim.tl/Tests/IJobEntityParityTests.cs (+ .meta),
run the EditMode suite, red/green until green, commit as the Phase 2 atom.
Harness design receipts: IJobEntityArmMatchesManagedArmForwardAndBackward,
IJobEntityDerivedQueryAppliesEligibilityPolicy,
DelayedSelectionDependencyBlocksCommitUntilCompletion (gate job holds an in-flight
RO read; coordinator must not commit while the selection dependency is incomplete;
commit lands exactly once after completion; the observe stage chained on the
published dependency reads the committed value).
