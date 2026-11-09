# Force demo (A)

A blue cube on a grid floor (1 m lines, numbered in metres). One Force clip pushes it right, fast at first, slowing, and it stops at
x 4.03 m. After a 1 s pause the Return timeline pushes it back to 0, then Push starts again, forever. The camera follows the cube. Top left:
`Push  1.2 s   force +3.2 X   x 2.84 m`.

## First 10 minutes

1. Package Manager > **Tl Unity Playback** > **Samples** > **Force demo** > **Import**. The scene **Force Demo** opens by itself
   (`Tl > Samples > Build Force Demo` rebuilds it). Press **Play** and watch one lap: right, pause, back, pause.
2. Tick the checkbox next to **Force Demo Objects** in the Hierarchy (opens the SubScene), select **Cube**,
   `Window > Sequencing > Timeline`. You see the **Push** timeline: one **Force** track, one 2 s clip, and a 1 s empty gap.
3. Press Play. Click the clip and set **Strength** to 10 in the Inspector: the next push goes about 10 m. Set it back to 4.
4. Stop Play. Drag the Timeline playhead: the cube moves in the Scene view without Play. Click away and it goes back.
5. Press Play and use the **Tl Panel** under the readout (top left, in the Game view): pick **Push** or **Return**, then **Pause**, **Resume**, **Restart**, **-1 s** / **+1 s** (seek), **Reverse**, **Stop**, and the **Speed** buttons (0.25x to 4x). Click **World: forward** to turn it into **World: rewinding**: the whole world runs backward and the cube walks back along its path. **Reverse** does the same for one timeline.
   Why the cube can walk back: the track ends with `* frame.Direction` (`+1` going forward, `-1` going backward), so the force it adds flips sign when time runs backward. Without that one factor the cube would keep going forward, because a force is added to the position and nothing takes it back. The cube comes back *close* to where it started, not to the last digit: the force is added once per frame, and the one tick where time turned around stays in. Things that must come back exactly (a bag, health) use pulses: see the Inventory and Stats demos and `Documentation~/REWIND-AND-SPEED.md`.
   Good to know: **Play** and **Restart** act on the timeline you picked. If that timeline is not the one running (after a world rewind you may be inside **Return**, because Push hands over to Return and back), they start an extra playback and the cube is pushed by both. Press **Stop all**, or stop and replay the scene, to get one clean playback.
6. Read `Force/ForceKind.cs` (16 lines): the kind adds up every Force clip active on the cube into one value per tick, and the track turns
   a clip into that value.

## What the designer builds (Timeline window, no code)

| Asset | Content |
|---|---|
| `Push.playable` | Force track, one clip 0-2 s: Direction (1, 0, 0), Strength 4, Falloff a falling line. Timeline Inspector: Duration Mode **Fixed Length**, 3 s (the last second is the pause). Tl section: When it ends **Next**, Next **Return**. |
| `Return.playable` | The same with Direction (-1, 0, 0). When it ends **Next**, Next **Push**. |
| Cube (SubScene) | Playable Director with Push, Play On Awake on. **Follow Target** component (the camera follows it, the readout shows its force). |
| Camera Rig (SubScene) | **Follow Camera**: Offset (0, 3, -7), Sharpness 4. Its rotation is the camera's rotation. |
| Main Camera | **Camera From Entity** copies the rig's pose every frame. |
| Overlay | **Force Overlay** draws the top-left line. |

Return is reached through Push's Next, so it bakes onto the cube without being listed anywhere else.

## What the coder writes

`Force/` is the five-file contract from `docs/USING-ECS.md` section 2, unchanged: `ForceClip` (the clip fields), `ForceData`
(components), `ForceKind` (kind + track), `ForceBaking` (what a clip adds to its entity), `ForceSystem` (a Burst job that moves the cube).

`Presentation/` is what a real game adds around it, all Burst jobs scheduled from `ISystem`s:

| File | What it does |
|---|---|
| `FollowCamera.cs` | `FollowCameraSystem` (TransformSystemGroup, first) schedules `FollowJob`, which eases the rig towards the target plus the offset. |
| `FollowCameraAuthoring.cs`, `FollowTargetAuthoring.cs` | Inspector components and their bakers. |
| `ForceReadout.cs` | `ShowForce` copies the cube's summed force each tick before `ApplyForce` clears it, so the overlay can show it. |
| `CameraFromEntity.cs`, `ForceOverlay.cs` | The only MonoBehaviours: read one entity per frame (an `EntityQuery`, no names) for the camera and the text. |

`Control/DashWhenFar.cs` is a second scene (`Tl > Samples > Build Control Demo`): a Burst job starts a Dash timeline from a clip field when
the cube passes x 2.

## Checked headless

`Tests/ForceDemoTests.cs` (PlayMode) prints
`TL-DEMO force x=4.0333 back=0.0000 expected=4.0333 warnings=0 errors=0 picture=ok ...` and fails if the push is not 4.0333 m (±0.0005), the
return does not end at 0, the camera has not settled over the cube, the rendered cube is more than 2% of the frame away from where the
camera projects x 4.0333, or any warning or error is logged while it plays. The Edit-mode tests compare the Timeline preview with Play and
check live edit. The real Timeline-window clicks are listed in `docs/FORCE-DEMO-CHECK.md`.
