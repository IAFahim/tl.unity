# Owner decisions for the engine-agnostic work

Decided 2026-11-08 by the owner (delegated to the manager) on `docs/design/engine-agnostic.md` section 7 and
`docs/design/demo-set.md` section 5. Each item says what was decided and what it changes. Spike results that pick between a decision's
main path and its fallback are in `docs/design/engine-agnostic-spikes.md`.

## 1. Math type in packs

Tl gets its own `float2`, `float3`, `float4` (and the rest as packs need them) in namespace `Tl.Mathematics`: plain fields `x, y, z, w`,
the same layout as `Unity.Mathematics`, so the pair layout digest is equal. Unity code converts at the edge.

Fallback, if spike S-C shows Burst runs `Tl.Mathematics.float3` slower than Unity's `float3`, or if the five Force contract files would
need an edit: compile Tl source inside Unity with a conditional alias, so `float3` in Tl source means `Unity.Mathematics.float3` in Unity and
`Tl.Mathematics.float3` outside it. The rule for choosing: the five Force files stay unchanged and both pinned hashes stay identical. Which
one was taken, and why, is recorded under S-C in the spikes document.

## 2. Where kind packs live

In the public tl repository under `packs/` (MIT). No tags, no releases, no NuGet publish, no change of repository visibility.

## 3. Loose game code runs in `tl run`

Yes. Data made from clip types in a project's `Assembly-CSharp` (no asmdef) must also run in `tl run`. So pair keys drop the assembly name
and use the full type name (namespace and name, generic arguments the same way). Two different types with the same full name in one bake
or one library are an error with both assembly names in the message, never a silent pick. This changes the bytes of every TLB, so the byte
fixtures are re-pinned in the same commit; the state hashes stay identical: 13943614770702343869 (600 steps) and 6607956939547305625
(events).

Done in phase 4 (tl `f731c1de`): `Keying.StableName` and the generator front end drop the assembly name; the baker, the resolver and
the kind catalog fail on a full-name twin with both assembly names; every TLB, library and snapshot fixture was re-pinned, the
state hashes did not move.

## 4. Control is per playback and per channel

`Seek`, `SetSpeed`, reverse and the channel verbs act on one playback or one channel, not on the whole world (IAFahim/tl#577, plan
section 2.3). This is phase 6.

## 5. The playback panel

The package ships a `Tl Panel` component (no user MonoBehaviour needed), plus a sample that shows the hand-written way.

## 6. No backward compatibility

None is needed. Rename, move, delete and re-pin freely: no shims, no obsolete forwarding types, no old-name aliases, no compatibility
fallbacks; pick the cleanest final API. What still holds: both pinned state hashes stay identical (13943614770702343869 and
6607956939547305625), the five Force contract files compile unchanged (they fix the coder-side API shape), and the K bench stays in band.

Effect on work done so far: phase 1 added no shim (the Unity copies were deleted, not forwarded; `TlAssetIds.ToAssetId()/ToHash128()` is
the engine edge, not a compatibility layer). None of the spike results depends on a compatibility fallback: S-C took no fallback, and the
S-D rule (a kind and the tracks that feed it share one assembly) is a current generator limit, not a compatibility promise. Plan items that
existed only for compatibility are dropped: `[assembly: TlPack]` is no longer needed to keep old pair keys (decision 3 re-pins them), and
`tlb` does not need to stay as an alias of `tl`.
