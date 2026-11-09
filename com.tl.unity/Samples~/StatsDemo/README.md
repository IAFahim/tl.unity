# Stats demo (E): health and attack over time, through the Stats pack

A hero fights a 10 s duel written as a timeline: three hits of 12, poison (1 every 20 ticks for 4 s) and a heal of 20. A Haste buff
timeline (+10 attack for 5 s) starts at 1.67 s and again at 4.17 s; the second start restarts it, the bonus never stacks. Top left:
`Health 64 / 100   Attack 20 (haste +10)`.

Needs the Stats pack next to `com.tl.unity`: Package Manager > **+** > **Add package from git URL** >
`https://github.com/IAFahim/tl.git?path=/packs/Stats` (the Tl installers add it for you).

## First 10 minutes

1. Package Manager > **Tl Unity Playback** > **Samples** > **Stats demo** > **Import**. **Stats Demo** opens
   (`Tl > Samples > Build Stats Demo` rebuilds it). Press **Play** and watch the numbers: health 64 at 3.3 s, 53 at 7.5 s, 72 at 10 s.
2. Tick **Stats Demo Objects**, select **Hero**, `Window > Sequencing > Timeline`: **Duel** has a **Hit**, a **Poison** and a **Heal**
   track (Timeline **+** > **Stats**). Change a Hit clip's **Amount** in Play: the next hit uses it.
3. The **Tl Stats** component on the Hero sets the starting Health, Max and Attack. Tl keeps these values: it only adds the Stats
   components when nothing else did.
4. Read `Scripts/Haste.cs`: one Burst job starts the Haste timeline (dragged into **Haste Authoring** and **Tl Also Plays**) at duel ticks
   100 and 250.

## Pause, speed and rewind

The **Tl Panel** under the readout (Game view) controls the hero's timelines: **Pause**, **Reverse**, **Speed** 0.25x to 4x. Click
**World: forward** to rewind: Health climbs back and the Haste bonus drops off. Checked: after 300 steps forward and 300 steps back, Health is
100 and Attack is 10 again (and Health at step 400 equals Health at step 200). It stays exact because Health is not clamped: a clamp cannot be
undone, so a game clamps when it draws.

## What the designer builds

| Asset / object | Content |
|---|---|
| `Duel.playable` | Hit: three 1-tick clips at ticks 60, 120, 180, Amount 12. Poison: ticks 240-480, Amount 1, Every 20. Heal: tick 500, Amount 20. 10 s, **Hold**. |
| `Haste.playable` | Buff: 0-5 s, Attack Bonus 10. Channel 1, **Stop**. |
| Hero (SubScene) | Playable Director on Duel (Play On Awake), **Tl Also Plays** with Haste, **Tl Stats** (100, 100, 10), **Haste Authoring**. |

## How it works

The pack's `StatsDamage` kind sums hits, heals (negative damage) and poison of one tick into one Health write; `StatsAttackBonus` adds the
buff on clip start and removes it on clip end, also when a restart interrupts it. Rewind and seek replay these by the same rules.

## Checked headless

`Tests/StatsDemoTests.cs` (PlayMode, default world, the built scene) reads the hero at duel ticks 199, 449 and 599 (the .NET pack test's
steps 200, 450, 600) and requires Health/Attack 64/20, 53/20 and 72/10, a largest bonus of 10 and no warning or error. It prints
`TL-DEMO stats health200=64/20 health450=53/20 health600=72/10 ...`.
