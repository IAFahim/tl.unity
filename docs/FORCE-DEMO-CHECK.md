# Force demo: 3-minute human check

What the batch runs prove is listed in [zero-config-arch.md](design/zero-config-arch.md), "Phase 4 results". This page covers what a batch
Editor cannot show: the real Timeline window, Undo/Redo, duplicating and dragging. Nothing here is verified by an agent. Tick each line.

## Setup (once, about 1 minute)

1. Unity Hub > **New project** > **Universal 3D**, editor 7000.0.0a16. Open it.
2. `Window > Package Management > Package Manager` > **+** > **Install package from git URL** > paste
   `https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main` > **Install**.
3. In Package Manager select **Tl Unity Playback** > **Samples** > **Force demo** > **Import**.

## Check

1. [ ] The scene **Force Demo** opens by itself. If it does not, click `Tl > Samples > Build Force Demo`. Console: 0 errors, 0 warnings.
2. [ ] Press **Play**. In the Game view the cube slides to the right for 2 s, fast at first, slowing, and stops about 4 m from where it
       started. Stop Play.
3. [ ] Hierarchy: tick the checkbox next to **Force Demo Objects** (opens the SubScene), select **Cube**. Inspector shows **Playable
       Director** with **Push**, Play On Awake on. No Tl component was added.
4. [ ] `Window > Sequencing > Timeline`. The window shows a track named **Force** with one clip labelled **Force**.
5. [ ] Click the clip. Inspector, under Unity's clip header: **Direction** (X 1, Y 0, Z 0), **Strength** (4), **Falloff** (a curve; click
       it and Unity's curve editor opens). No text box asks for a name or a type.
6. [ ] Set Strength to 10, then press **Ctrl+Z**: Strength is 4 again. **Ctrl+Y**: 10. Set it back to 4.
7. [ ] Click **+** at the top left of the Timeline window. The list shows **Force** next to Activation Track, Animation Track and the
       others. Click **Force**: one new track named **Force** appears (no second, hidden or oddly named track).
8. [ ] Press **Ctrl+Z** once: the new track is gone completely. **Ctrl+Y**: it is back.
9. [ ] Right-click the new track > **Add Tl Clip**. A clip labelled **Force** appears; its Inspector shows Direction, Strength, Falloff.
10. [ ] Select the first Force track, **Ctrl+D**. The copy has its own clip: change its Strength to 1; the original clip still says 4.
11. [ ] Drag the original clip so it starts at 1 s. Delete the two extra tracks (select, **Delete**). Press **Play**: the cube waits about
        1 s, then slides. Stop Play, **Ctrl+Z** until the clip starts at 0 again, `File > Save`.
12. [ ] Project window: select `Push.playable`, **Ctrl+D**. Double-click the copy: it has the Force track and clip with the same values.
13. [ ] Close Unity, open the project again, open Push in the Timeline window: the clip still shows Strength 4 and the curve.

14. [ ] Open `Push` in the Timeline window (Cube selected, SubScene open). Drag the playhead: the cube moves in the Scene view while you drag,
        without Play, and stands about 4 m to the right at the end. Click away from the Timeline window or close it: the cube is back at the start
        and the scene title shows no `*`.
15. [ ] With the playhead at the end, change Strength to 8 in the clip Inspector: the cube jumps to about 8 m. **Ctrl+Z**: back to about 4 m.
16. [ ] Press **Play**. While the cube is moving, change Strength to 10: the cube speeds up within a second and Play does not restart. Stop Play.
17. [ ] Select `Push.playable` in the Project window. Under Unity's header there is a **Tl** section: Channel, When it ends, Status
        `OK. 1 Tl track, 1 clip.`. Set When it ends to Loop, then **Ctrl+Z**.
18. [ ] Select Cube. Below the Playable Director there is a **Tl** foldout with **Also Plays** and **Plays: Push**. No other Tl component is
        visible.
19. [ ] Drag the Cube out of the SubScene into the main scene: the foldout says it is not in a SubScene. **Ctrl+Z**.

20. [ ] `Tl > Samples > Build Control Demo`, press Play: the cube slides right and, once past about 2 m, also slides away from the camera
        (a Burst job started the Dash timeline). Select its clip **Dash When Far**: the Inspector shows a **Dash** timeline field (drag and drop)
        and **At X**.

Report on issue #22: the numbers of any unticked lines and what you saw instead.
