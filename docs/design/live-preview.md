# Edit-mode preview and live edit (phase 5)

## What the two references do

BovineLabs Timeline (tertle, `BovineLabs.Timeline.Editor/EditorTimelineSystem.cs`, read at 9f0de90):

1. Its whole timeline runs as ECS systems, and its `TimelineSystemGroup` also runs in the Entities Editor world (`WorldSystemFilterFlags.Editor`).
2. The Editor world holds the live-baked entities of open SubScenes, so the same jobs that run in Play run there.
3. An editor-only system (a `SystemBase`) reads `TimelineWindow.instance.state.masterSequence.director.time` every frame.
4. It finds the director's entity with `EntityManager.Debug.GetEntitiesForAuthoringObject` and requests the timer to that time.
5. Its clips evaluate as functions of time (sampled tracks), so a jump to any time is one evaluation; there is no history.
6. Every timeline entity is reset when it is not selected (`TrackResetOnDeactivate`), which is how leaving the scrub restores the scene.
7. What the designer sees is the Editor world drawn by Entities Graphics (runtime data in the Scene view), not the GameObjects.
8. Taken: run the real runtime code, not a copy, and treat "deselected" as "restore".
9. Not taken: a `SystemBase` in the Editor world (owner rule), and the Editor world itself: Tl kinds integrate over ticks (Force adds a
   push every tick), so a scrub needs the ticks before it, which the shared Editor world cannot rewind.
10. Not taken: depending on the Scene view showing runtime data; by default it shows authoring GameObjects.

TweenPlayables (annulusgames, `Runtime/Core/TweenAnimationTrack.cs`, `TweenAnimationMixerBehaviour.cs`):

1. Classic Timeline: each track's `CreateTrackMixer` returns a `ScriptPlayable` mixer; Timeline calls its `ProcessFrame` while scrubbing.
2. Edit-mode preview is Timeline's own: when the Timeline window previews, it evaluates the graph at the playhead every repaint.
3. The mixer writes plain values (position, color) straight onto the bound component.
4. `TrackAsset.GatherProperties` registers every property it will touch with `driver.AddFromName<T>(gameObject, path)`.
5. Timeline snapshots those properties when preview starts and puts them back when it stops, so the scene is never dirtied.
6. Values are pure functions of clip progress (ease curves), so any playhead time is one evaluation.
7. Taken: the mixer as the hook (no extra component), `GatherProperties` for restore, and writes only inside Timeline's preview.
8. Taken: the result lands on the GameObject the designer selected, so the Scene view shows it without an Entities setting.
9. Not taken: evaluating clip values in the mixer; that would be a second implementation of Tl's clip and kind logic.
10. Not taken: per-type mixer classes; Tl has one `TlTrack` for every clip type.

## The design

- `TlTrack.CreateTrackMixer` returns `TlPreviewMixer` (package, runtime assembly). Its `ProcessFrame` does nothing in Play; in Edit mode it
  calls `TlPreview` (package, Editor) with the director being previewed. `TlTrack.GatherProperties` registers the director object's
  `Transform` (position, rotation, scale), so Timeline restores it when preview ends.
- `TlPreview` bakes the director's GameObject with the real `TlDirectorBaker` and the coder's `ITlBaker` into a private world (cached; it
  bakes again only when the director, a timeline, a Tl track, a clip, the settings marker or Also Plays changed, read from
  `EditorUtility.GetDirtyCount`, so an Inspector edit or Undo shows at once).
- For each new playhead tick it copies the baked entities into a fresh private world holding Tl's library, register and Play On Awake
  systems plus every system in `TlSimulationGroup` (Tl's request, kind tick and apply systems and the coder's own, here `ForceSystem`), then
  steps them at the project tick rate (60 per second) until the playback reaches the playhead tick, exactly as the fixed step does in Play.
  The object's `LocalTransform` is written to its `Transform`, registered with `AnimationMode` first.
- A playhead at or past the end of a timeline shows the state after its last tick: when the playback hands over to the next timeline (When it ends: Next), the step is dropped and the earlier state is kept, so Push at 3.0 s is x 4.0333 as in Play, not the chained Return timeline's x.
- Nothing is re-implemented: the same baked blob, library assembler, Burst kind jobs and coder systems run; only the world is private.
- Writes happen only while Timeline is previewing (`AnimationMode`). Leaving preview restores the Transform; the scene is not dirtied.
- The coder writes nothing for it (no file, attribute, asset or step); the five Force files preview as they are.

Measured headless (`ForceDemoPreviewTests`, Edit mode, director time set by script, Timeline's `AnimationMode` on): x at ticks 30, 60 and 90
is 1.8083, 3.0500, 3.7917 in preview and in Play; the end of Push is 4.0333 in both; Strength 8 previews 8.0667; Undo returns 4.0333;
after preview the cube is back at x 0 and the SubScene is not dirty. Picture of the preview at the end of Push:
[force-preview-end.png](phase5/force-preview-end.png).

What preview does not show:

- Only the director object's own `Transform` is written back. Other components (`Force`, health, buffers) change in the private world but
  have no GameObject value to show.
- Systems outside `TlSimulationGroup` (physics, transform hierarchy, other packages' groups, systems in `SimulationSystemGroup`) do not run.
- Objects under a parent are written in local space; children of the director are not written.
- The SubScene must be open (its checkbox ticked) so the GameObject is loaded; a closed SubScene has nothing to show.
- The Game view in Edit mode draws the Editor world's entities, which Entities rebakes from the moved Transform; the Scene view shows the
  preview directly. Only the Scene view picture is verified here.
- A long timeline costs one world step per tick for each new playhead position (Push, 120 ticks: a few milliseconds).

## Live edit in Play

Spike S4 (Windows, 7000.0.0a16): editing a clip in Play rebakes the open SubScene and patches the runtime entity in 9 to 40 ms. The patch
replaces the `TlTimelineSet` buffer (ids back to 0) and leaves `TlUnregistered`, `TlPlayOnAwake` and `Playback` untouched. So
`TlTimelineRegisterSystem` now also looks at sets whose buffer changed (a change filter, nothing per frame when nothing changed) and
re-registers elements with id 0. A changed timeline keeps its id; every playback of it keeps its tick (clamped to the new length), its kind
entries are dropped and rebuilt at that tick by a Burst `TlTimelineSwapJob`, so no stale span keeps pushing. An edit that adds or removes a
type of track (the timeline would reach other kinds), or a stale bake after a script change, is refused with one console error and the
earlier version keeps playing until Play is restarted.
