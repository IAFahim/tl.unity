# Motion demo (C): values over time on transforms and colours, through the Motion pack

Three objects, no game code. The blue **Cube** glides left to right and back. The **Sphere** breathes (scale 1 to 1.5 and back) and
turns red to yellow and back. The green **Capsule** spins while it hops; between 0.67 s and 1.33 s a second Move clip with
**Priority 5** pulls it to the right, then lets go.

Needs the Motion pack next to `com.tl.unity`: Package Manager > **+** > **Add package from git URL** >
`https://github.com/IAFahim/tl.git?path=/packs/Motion` (the Tl installers add it for you).

## First 10 minutes

1. Package Manager > **Tl Unity Playback** > **Samples** > **Motion demo** > **Import**. **Motion Demo** opens
   (`Tl > Samples > Build Motion Demo` rebuilds it). Press **Play**.
2. Tick **Motion Demo Objects**, select **Sphere**, `Window > Sequencing > Timeline`: **Breathe** has a **Scale** and a **Tint** track.
   In Play, select the Tint clip and change **To**: the sphere takes the new colour on its next loop.
3. Stop Play. Select **Cube**, open **Glide**, Timeline **+** > **Motion** > **Spin**, add a clip (right-click the track > **Add Tl Clip**),
   set **To Yaw** 360, press Play: the cube spins while it glides.
4. Scrub **Glide** in Edit mode: the cube moves. Colours are not previewed in Edit mode.
5. Select **Capsule**, open **Hop**: the **Pull** track's clip has **Priority 5**, higher than the **Jump** track's 0, so it wins while
   both play.

## What the designer builds

| Asset / object | Content |
|---|---|
| `Glide.playable` | Move track: 0-1 s From (-2,0,0) To (2,0,0); 1-2 s back. Ease a straight ramp. 2 s, **Loop**. |
| `Breathe.playable` | Scale track: 0-1.5 s From 1 To 1.5, Ease a triangle (up and back). Tint track: red to yellow, same Ease. 1.5 s, **Loop**. |
| `Hop.playable` | Spin 0 to 360 over 2 s; Jump (4,0,0) to (4,1,0) with a triangle; Pull (4,0,0) to (5.5,0,0) from 0.67 s to 1.33 s, Priority 5. 2 s, **Loop**. |
| Cube, Sphere, Capsule (SubScene) | A Playable Director on their timeline, Play On Awake. Nothing else. |

## How it works

- The pack (`Tl.Pack.Motion`, plain C# in IAFahim/tl) declares the four clips, four kinds (`MotionPosition` with Priority,
  `MotionScale`, `MotionYaw`, `MotionTint`) and the `Pose` and `Tint` state. It compiles in .NET and in Unity unchanged.
- Tl adds `Pose` to an object whose timelines reach a Move, Scale or Spin clip and `Tint` only to one that reaches a Tint clip.
- The Unity adapter (`com.tl.unity/Packs/Motion`, compiled only when the Motion pack is installed) adds `PostTransformMatrix` (identity)
  and `URPMaterialPropertyBaseColor` (white) at bake time, and its Burst jobs copy `Pose` to `LocalTransform` / `PostTransformMatrix` and
  `Tint` to the URP base colour after Tl's step. `Pose.Has` keeps the object's own transform until a track first writes it.

## Checked headless

`Tests/MotionDemoTests.cs` (PlayMode, default world, the built scene) samples every frame for 200+ samples and checks, bit for bit, the
Cube and Capsule positions and the Sphere scale against the same formulas the .NET pack test uses (Tl.Packs.Tests
`MotionTests.PositionsMatchAScalarReferenceAndTheOverrideWinsByPriority`), including the .NET checkpoints (ticks 29, 59, 89, 119), and
prints `TL-DEMO motion samples=... mismatches=0 ...`.
