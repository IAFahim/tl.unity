# Events demo (G): clip events drive gameplay

A red knight plays an attack combo on a loop. Each Strike clip makes the knight lunge, and at the moment the clip starts it raises a
`Strike` event. Gameplay code reads the strike and: sends 6 damage to the training dummy, starts the dummy's own **Hurt** timeline (it
squashes), spawns a spark, and counts the strike. Top left: `Strikes 7   Dummy health 58 / 100   Sparks 7`.

## First 10 minutes

1. Package Manager > **Tl Unity Playback** > **Samples** > **Events demo** > **Import**. **Events Demo** opens
   (`Tl > Samples > Build Events Demo` rebuilds it). Press **Play**: three strikes every 2 s, a spark and a squash on each.
2. Tick **Events Demo Objects**, select **Knight**, `Window > Sequencing > Timeline`: **Combo** has one **Strike** track with three
   clips. Select **Dummy**: **Hurt** has one **Squash** track.
3. In Play, select a Strike clip and set **Amount** to 20: the dummy loses 20 per hit from the next strike on.
4. Stop Play, add a fourth Strike clip to Combo (right-click the track > **Add Tl Clip**), press Play: one more spark per loop.
5. Read `Scripts/StrikeSystem.cs` (one Burst job does all four reactions).

## What the designer builds

| Asset / object | Content |
|---|---|
| `Combo.playable` | Strike track (`Arena/Strike` in the **+** menu), clips at ticks 20, 50, 80 (10 ticks each): Amount 6, Reach 0.6. Fixed Length 2 s, When it ends **Loop**. |
| `Hurt.playable` | Squash track (`Arena/Squash`), one 0.3 s clip: Amount 0.35, Shape a bump. When it ends **Stop**. |
| Knight (SubScene) | Playable Director on Combo, Play On Awake. **Arena Attacker**: drag the Dummy into **Target**, `Spark.prefab` into **Spark**, `Hurt.playable` into **Hurt**. |
| Dummy (SubScene) | Playable Director on Hurt, Play On Awake **off** (so Hurt bakes onto it, waiting to be started). **Arena Dummy**: Health 100. |
| `Spark.prefab` | A small glowing sphere with **Arena Spark** (Life 0.4 s). |

## What the coder writes

| File | Lines that matter |
|---|---|
| `Strike.cs` | `StrikeClip` (Amount, Reach). `StrikeTrack` feeds the `ArenaLunge` kind (the knight's forward step) and, in `OnClipStart(in TlFrame<StrikeClip>, ref Events<Strike>)`, raises one `Strike` with the clip's Amount when the clip starts (forward only). Tl adds a `TimelineEvent<Strike>` buffer to the knight by itself. |
| `Dummy.cs` | `ArenaDamage`: a kind that accepts **game pulses** (`Accepts = Pulses, GamePulses = 8`) and subtracts the sum from `DummyHealth` once per tick. `SquashTrack` feeds `ArenaSquash` for the Hurt timeline. |
| `StrikeSystem.cs` | Runs last in `TlSimulationGroup`, after Tl applied the tick's kinds. Its job reads the knight's `TimelineEvent<Strike>` buffer and, per strike: `ArenaDamageKind.Pulses(writer).To(target, amount)`, `requests.Start(attacker.Hurt)` on the dummy's request buffer, an ECB `Instantiate` of the spark at the dummy, `score.Strikes++`; then clears the buffer. |
| `ArenaAttackerAuthoring.cs` | Bakes `Attacker { Target, Spark, Hurt }`. `TlBaking.GetTimeline(this, authoring.Hurt)` turns the dragged timeline into a `TlTimeline`. |
| `ArenaDummyAuthoring.cs` | Bakes `DummyHealth`, `Wounds` and the `ArenaDamage` kind's buffers (`ArenaDamageKind.Types`), because no timeline on the dummy feeds that kind; Tl adds a kind's components only to objects whose timelines reach it. |
| `ArenaPresentation.cs` | Burst jobs: lunge to position, squash to scale, spark fade and destroy. |
| `EventsOverlay.cs` | The only MonoBehaviour: reads `Score` and `DummyHealth` for the text. |

The event path: clip start -> the track's `OnClipStart` raises `Strike` into `Events<Strike>` -> Tl writes one record per strike, in
order, into the knight's `TimelineEvent<Strike>` buffer in its kind job -> your job reads it the same tick (inside `TlSimulationGroup`,
after Tl's systems). Tl refills the buffer every tick, so each strike is seen once. Damage sent this tick is applied by Tl at the next
tick, together with any timeline damage, as one write.
What not to do: no `SystemBase`, no managed callbacks or `UnityEvent`s, no searching objects by name; drag references into the authoring
components instead.

## Checked headless

`Tests/EventsDemoTests.cs` (PlayMode, default world, the built scene) runs 600 Tl steps (10 s) and prints
`TL-DEMO events strikes=15 health=10 hurt=15 sparks=15 ...`. It fails unless there are exactly 15 strikes (5 loops x 3), dummy health
100 - 15 x 6 = 10, 15 Hurt starts and 15 sparks, health equals 100 - 6 x strikes at every frame (one tick late at most), and no warning or
error is logged.

Not covered yet: the CLI events hash for the same timelines (waits for the engine-agnostic `tl run`).
