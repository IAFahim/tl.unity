# Tl kinds in Unity (`com.tl.unity`)

How game-defined per-step work runs on the Unity host: the kind model, the
step order, sizing (capacities, kind sets), the Kind Report, and the samples
with their cross-host hashes.

## Kind model

A kind is a value type plus a `Combine` rule (or `Each`) plus an `Apply`
method. A clip feeds a kind through a `[Feeds]` method evaluated every step
while the clip is active; a `[Pulse]` method fires a one-step value at clip
start, clip end, or a marker. Game code sends one-step values through
`KindPulses<K>.To(entity, value)`. The test game (`Tests/Kinds/Game`) defines:

| Kind | Value | Combine | Apply target | Fed / pulsed by |
|---|---|---|---|---|
| Locomotion | `Speed` (`Value`, `Priority`) | highest priority wins | `Motion.X` | `Walk.Evaluate` (reads `WalkStat`, `Target`, `AnchorLookup`) |
| Lift | `float` | sum | `Motion.Y` | `Jump` fold + `Evaluate` (reads `JumpPower`) |
| Damage | `int` | sum | `Health` | `Hit` start pulse; game code via `KindPulses<int>.To` |
| StatChange | `int` | each applied once | `Stamina` | `ChangeStat` start pulse, `Steps` marker pulse |

`Locomotion` shows overriding by priority (a faster high-priority entry
shadows the walk until its clip ends). `Lift` shows stacking (jump plus two
potion hops sum into one value per step). `Damage` shows game-code pulses
summed with timeline hit pulses into one `Health` write per step. `StatChange`
shows `Each` mode: every pulse applies exactly once, in a fixed merge order.

## Step order

Each step runs `TimelineKindTickSystem` first, then the kind loops inside each
system (`Movement` runs Locomotion, Lift, StatChange; `Vitals` runs Damage):

1. `Tick`: apply start/stop requests, advance playback ticks, maintain span
   entries (span start/end from boundaries, marker pulses), merge queued game
   pulses in a deterministic order, handle end/next/hold/finish, maintain the
   `Active`/`Enabled` row state.
2. Kind loops: one loop per kind per chunk over that kind's entry buffer.
   Rows whose kind row is disabled (`Enabled` off, or `Active` off) contribute
   nothing but keep their entries.
3. `Apply`: each kind writes its owner's data (`Motion`, `Health`, `Stamina`).

Send game pulses before the step (`Damage(t, v)` then `Step()`); they merge
during that step's Tick. Pulses to an entity without the kind are counted in
`Tick.DroppedPulses` and dropped, as are starts for unreachable timelines
(`Tick.DroppedStarts`).

## Sizing

`Kinds.g.cs` pins the inline capacity per kind (test game: Damage 3, Lift 2,
Locomotion 2, StatChange 3). The generator sizes each entry buffer from it;
buffers that overflow spill to the heap and `TrimExcess` moves them back
inline once the pulses are applied (measured working on Unity in K0).
Rules of thumb: tens of kinds, not hundreds; capacity 0 for rare kinds; let
the report guide the rest.

Every entity pays a header plus inline entries for each kind it can reach.
Baking assigns exactly the reachable kinds (`Editor/TimelinePlayerKindsBaker`:
fed kinds from the reachable-timeline walk, pulsed kinds from the walk, game
kinds from `[RequiresKinds]` on the baked components, plus the union of the
named profile). `TimelineKindLibraryAsset.KindSets` picks the layout:

- `Exact`: each archetype carries exactly its reachable kinds (least memory,
  more archetypes).
- `UnionPerProfile`: entities sharing a profile name carry the union of the
  profile's kinds (fewer archetypes, more bytes per entity).

The editor bake writes `Kinds.g.cs` through `tlb kinds --library <path>
--namespace <ns>` and refreshes the AssetDatabase only when the file changed,
so capacities recompile only when a worst case actually changes. The `tlb
kinds` subcommand is owned by TlbKinds in the tl repo and its argv contract
is not frozen yet; until it lands, the bake fails fast with tlb's stderr and
touches no file.

## Kind Report

`Tools/Tl/Kind Report` captures one row per archetype that carries kinds
(`KindReport.CaptureRows` over the default world's archetypes, bytes from
`SizeInChunk`) and applies three rules:

- `TLKIND001`: more than 32 kinds on one archetype — split the set or add a
  profile.
- `TLKIND002`: more than 1024 kind bytes per entity — group rare kinds or
  lower capacities.
- `TLKIND003`: fewer than 16 entities per chunk — the archetype fragments
  chunks; share a profile with a bigger set.

## Samples

Both samples live in `com.tl.unity/Samples~`, run headless (`-batchmode`
quits 0 on match, 3 on mismatch), and print `hash expected match backend`
receipts.

- `KindsIL2CPP` (`KindsSample`): the Runner on kinds. A speed boost overrides
  the normal walk by priority (X 2 then 7, two entries, 102 over 19 steps,
  run takes over at 104 with one entry), lifts add up (Y 44, then 84 with a
  stacked potion hop), stat pulses apply once (Stamina -1, then 9 from the
  marker). Then the 600-step random script: hash `13943614770702343869`,
  equal to the .NET `KindUnityHashTests` run.
- `BulletsIL2CPP` (`BulletsSample`): game-code `Damage` pulses plus a `Stomp`
  timeline's hit pulses summed into one `Health` write per step (993 after the
  game pulse, 955 after two game pulses land with the timeline hit, holds at
  955, 945 on the second hit, untouched rows stay 1000, zero dropped
  pulses). Step hash over all four rows' `Health`: `3081412629212866412`,
  reproduced from the .NET `KindRunTests` bullets script.

## Legacy mixing

Plan-1 `ExecuteActive` consumers keep plan 1's order inside one implicit
legacy kind that runs first in its system unless `Order` says otherwise.
Mixing legacy consumers and kinds that write the same field changes the order
between those two groups. On Unity, legacy consumers are not dispatched yet
(`TLUNITY9105`: feed a kind instead). Whether the legacy implicit kind and
`ExecuteActive` stay is the owner's K5 decision on IAFahim/tl#558.
