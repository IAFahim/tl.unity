# Burst Combat

Add `AttackAuthoring` to a GameObject in a SubScene. Baking creates one entity with playback, input pose, output pose, and health components. `AdvanceAttackSystem` schedules a Burst job that borrows those component fields for exactly one generated timeline call.

`Attack.Generated.cs` is representative output from the Unity backend. Its hot path uses immediate constants and direct static calls. `AttackBlob.Generated.cs` builds the immutable inspection and tooling artifact. The scalar kernel does not read the blob.
