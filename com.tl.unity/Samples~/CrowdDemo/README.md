# Crowd demo (H): many entities, one timeline, a bench row

A field of 10,000 small capsules dances: two hops, a spin and a glow every 2 s. Each dancer starts the same Dance timeline at a different
tick (from its grid position), so the field ripples. Top left: the dancer count and the Tl step time; buttons switch to 1k, 10k or 100k.

## First 10 minutes

1. Package Manager > **Tl Unity Playback** > **Samples** > **Crowd demo** > **Import**. **Crowd Demo** opens
   (`Tl > Samples > Build Crowd Demo` rebuilds it). Press **Play**.
2. Click **100k**: the crowd respawns with 100,000 dancers; read the Tl step time (needs the Profiler counters, so it shows in the Editor
   and development players).
3. Stop Play. Select `Scenes/Dancer.prefab`, `Window > Sequencing > Timeline` with the prefab open: **Dance** has Hop, Spin and Glow tracks.
   Make the Spin clip 2 turns, press Play: every dancer spins twice.
4. `Tools > Tl > Kind Report` lists the three kinds and their inline capacity.
5. Read `Scripts/Crowd.cs`: the spawn runs once per size change, so the per-frame work is Tl's systems plus one pose job.

## What the designer builds

| Asset / object | Content |
|---|---|
| `Dance.playable` | Hop track (`Crowd/Hop`, two 0.5 s clips at 0 s and 1 s, Height 0.6), Spin track (`Crowd/Spin`, 0.5-1 s, Turns 1), Glow track (`Crowd/Glow`, 0-2 s). Fixed Length 2 s, When it ends **Loop**. |
| `Dancer.prefab` | Capsule, Playable Director on Dance with Play On Awake **off**, **Dancer Authoring** (colour). |
| Crowd (SubScene) | **Crowd Authoring**: drag `Dancer.prefab` into **Prefab**, `Dance.playable` into **Dance**, set **Count** and **Spacing**. |

## What the coder writes

| File | Lines that matter |
|---|---|
| `Dance.cs` | Three clips, three kinds (`CrowdLift`, `CrowdYaw`, `CrowdGlow`, each summing into `DancePose`), three one-line tracks. |
| `Crowd.cs` | `CrowdSpawnSystem` (Burst `ISystem`): when `Crowd.Count` changes it destroys the old dancers, instantiates the prefab `Count` times and schedules `PlaceDancers`, a parallel `IJobEntity` that sets each home position and calls `requests.Start(crowd.Dance, offset)`. `DancePoseSystem` (fixed step, after Tl) schedules `PoseDancers`: pose to `LocalTransform` and URP base colour. |
| `CrowdAuthoring.cs` | Bakes `Crowd` with `TlBaking.GetTimeline(this, authoring.Dance)` and `TlBaking.Ticks(authoring.Dance.duration)`. |
| `CrowdOverlay.cs` | The only MonoBehaviour: count, the `Tl.TlSimulationGroup` profiler time, and the size buttons (they write `Crowd.Count`). |

Why it scales: there is no entity per clip; each dancer has one playback, and each kind reduces all of the dancer's entries to one write
per tick. Start offsets come from the grid position, not a random generator, so the .NET host can place the same starts.

## Checked headless

`Tests/CrowdBenchTests.cs` (PlayMode, Burst on) spawns 10,000 then 100,000 dancers, waits until every one plays Dance, warms up, then steps
`TlSimulationGroup` 200 times twice (completing jobs after every step) and keeps the second run. It prints
`TL-BENCH crowd entities=<n> ns/entity=<x> warmB=0 ...` and writes `Logs/CrowdBench.txt`. It fails unless the dancer count is exact, every
dancer plays, the warm allocation is 0 B and ns/entity is below 100.

Reference numbers (Windows 11, 7000.0.0a16, editor, Burst on, safety checks on): see the commit that added this demo and
`docs/design/demo-set.md`.

Not covered yet: the `Pose` hash compared with `tl bench demos/crowd --hash` in .NET (waits for the engine-agnostic CLI).
