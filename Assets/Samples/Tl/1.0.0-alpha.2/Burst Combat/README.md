# Burst Combat

Add `AttackAuthoring` to a GameObject in a SubScene. Baking creates one entity with playback, input pose, output pose, and health components. `AdvanceAttackSystem` schedules a Burst job that borrows those component fields for one generated signed seek.

`Attack.Generated.cs` is representative C# 9 output from the Unity backend. Its timeline-specific facade exposes `Start(gameTick)` and `TrySeek(ref data, delta)` over direct static calls. `AttackBlob.Generated.cs` builds the immutable inspection and tooling artifact. The scalar kernel does not read the blob.
