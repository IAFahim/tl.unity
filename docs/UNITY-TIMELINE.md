# Unity Timeline baking, preview and live edit

How a Timeline asset made in Unity's Timeline window becomes data that Burst systems play. The coder side is in
[USING-ECS.md](USING-ECS.md); the design notes are in [design/zero-config-arch.md](design/zero-config-arch.md) and
[design/live-preview.md](design/live-preview.md).

## Authoring objects

| Object | Where | What it holds |
|---|---|---|
| `TlTrack` | sub-asset of the `.playable` | the clip type (`Template`, a generated `<Clip>_TlData` mirror). One package class for every clip type. |
| `TlClip` | sub-asset | the clip's values as the same mirror (`[SerializeReference]`), so Unity's Inspector, Undo, prefab and copy logic apply |
| `<Clip>_TlMenu` | `Assets/Tl Generated/Editor/<Clip>_TlMenu.cs`, written by Tl, never saved in a timeline | the **+** menu entry; picking it creates a real `TlTrack` in one Undo step. Timeline wants every track class in a file named after the class, so Tl writes one tiny file per clip (rewritten or deleted when the clip changes; commit or ignore it) |
| `TlTimelineSettings` | marker at time 0, made by the Tl section on the first change | When it ends (Stop, Loop, Hold, Next), Play next, Channel. Duplicating or renaming the `.playable` keeps it. |
| `PlayableDirector` | the object, in a SubScene | Playable, Play On Awake, Initial Time. Unity's Wrap Mode is not used. |
| `TlAlsoPlays` | hidden component, edited in the Tl foldout | more timelines the object can play |

Mirror field rules: numbers, `bool`, enums and `float2/3/4` copy as they are; `TlCurve` is an `AnimationCurve` (sampled to 16 points);
`TlTimeline` and `TimelineId` are Timeline object fields. If the clip type's code is missing (compile error, rename), the values are kept and
the clip Inspector says so.

## Baking (`TlDirectorBaker`)

1. Timelines of the object, each once, in this order: the Playable, Also Plays, then every timeline reached through Play next and through
   `TlTimeline`/`TimelineId` clip fields. Each one, its Tl tracks, their clips and the settings marker get a `DependsOn`, so editing a clip
   rebakes only the objects that play it. Timelines without Tl tracks are skipped.
2. Per timeline: Tl tracks top to bottom (groups flattened, muted tracks skipped), clips by start. Times become ticks:
   `round(seconds * ticks per second)`, with the rate from `Project Settings > Tl` (default 60). Each clip's `ITlBaker`s run once on the
   object.
3. The timeline is packed in process (no tool, no file) into one blob: TLB bytes, the per-timeline kind data, the asset id, a content hash,
   the tick rate, the timelines it names and where its `TimelineId` values sit. The same timeline on many objects is one blob.
4. The entity gets `TlTimelineSet` (the blobs), `Playback`, `TimelineRequest`, `TimelinePlaying`, Play On Awake, the entry buffers of every
   kind its timelines reach, and every component those kinds write.

At run time `TlTimelineRegisterSystem` assembles one library from all loaded blobs (sorted, so ids never move when a SubScene streams in
later), writes the ids into `TlTimelineSet` and patches `Next` and `TimelineId` values to library ids. Two SubScenes with the same timeline
share it. Mixed tick rates are refused with a message.

## Live edit in Play

With the SubScene open (its checkbox ticked), Unity rebakes the object when a clip, track or setting changes, and patches the running
entity (measured 0.04 s). Tl re-registers the timeline under the same id; each playback of it keeps its tick (clamped to the new length);
its kind entries are rebuilt at that tick. A change that makes the timeline reach other kinds (a track of another type added or removed),
or a bake from before a script change, is refused with one console error and the old version keeps playing.

## Preview without Play

When the Timeline window previews (playhead dragged, clip edited), `TlTrack`'s mixer asks Tl to show the object at the playhead tick. Tl
bakes the object into a private world (cached until something changes), runs the real Tl systems and every system in `TlSimulationGroup`
for the ticks up to the playhead, and writes the object's resulting `LocalTransform` onto its Transform. Timeline restores the Transform
when preview ends; the scene is not marked changed.

Not previewed: components other than the object's own Transform, systems outside `TlSimulationGroup` (physics, transform hierarchy,
other packages), children of the object, and objects in a closed SubScene. The Scene view shows the preview; the Game view in Edit mode
draws Entities' own Editor world.

## Plain .NET and other hosts

`Tl > Export Library` writes the selected timelines (and the ones they name) as one `game.tlib`, the format the plain host loads. See
[USING-PLAIN-CSHARP.md](USING-PLAIN-CSHARP.md).

`Tl > Export` writes the same selection as portable JSON into a folder: one `<timeline>.json` per timeline and a `tl.project.json`
(schema 1, the format of tl's `docs/json-format.md`). It goes through the same packer as baking, so `tlb bake tl.project.json` on that
folder bakes each timeline to the same TLB bytes the Editor packs (checked by `ExportWritesJsonThatBakesToTheSamePackedBytes`), and `tlb decode` gives the JSON back. Channels are written as their numbers (`"1"`, `"2"`).
Kinds are identified by full type name (`Tl.Pack.Force.ForceKind` and a game's `ForceKind` are two kinds).

## Not supported yet

- Bindings to other scene objects from a clip.
- Starting values per object (components get their defaults; set them in your own baker).
- Off-grid clip warnings: clips are rounded to ticks silently.
- Changing the tick rate does not rebake open SubScenes; reimport them.
