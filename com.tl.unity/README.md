# Tl for Unity (`com.tl.unity`)

Timelines for Unity ECS with Burst. Designers build timelines in Unity's Timeline window with Tl tracks; a Playable Director in a SubScene
bakes them; Burst `ISystem`s play them on unmanaged data. Several timelines play on one entity, rewind is exact, and the editor shows the
result while you scrub, without Play.

Tested on the newest Unity 7 editor (7000.0.0a16), Entities 7.0.0, Entities Graphics 7.0.0, URP.

## Install

Use a **Universal 3D** (URP) project: Entities Graphics draws SubScene objects only under URP or HDRP. Then Package Manager > **+** >
**Install package from git URL**:

```
https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main
```

or in `Packages/manifest.json`:

```json
"com.tl.unity": "https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"
```

Nothing else: no tool, no .NET install, no asset, no asmdef edit. The installers in IAFahim/tl (`install/unity.ps1`, `install/unity.sh`)
create a URP project with the package and the Force demo ready.

## First minute

1. Package Manager > Tl Unity Playback > Samples > **Force demo** > Import. The scene opens.
2. Press Play: the cube is pushed about 4 m to the right.
3. Open `Push` in the Timeline window and drag the playhead: the cube moves without Play. Change Strength in the clip Inspector.

## Write your own track

Five short files: a `[TlClip]` struct (the clip Inspector), components, a `[TlKind]` with `Combine` and `Apply`, an `ITlTrack<TClip>`
with a `[Feeds]` method, and optionally an `ITlBaker<TClip>` and your own Burst system. The full text is in
[docs/USING-ECS.md](https://github.com/IAFahim/tl.unity/blob/main/docs/USING-ECS.md).

Start, stop, pause and resume from Burst jobs with `TlRequests` on the object's `DynamicBuffer<TimelineRequest>`, using `TlTimeline`
fields the designer fills by drag and drop.

## Guides

- [USING-ECS.md](https://github.com/IAFahim/tl.unity/blob/main/docs/USING-ECS.md)
- [UNITY-TIMELINE.md](https://github.com/IAFahim/tl.unity/blob/main/docs/UNITY-TIMELINE.md)
- [USING-PLAIN-CSHARP.md](https://github.com/IAFahim/tl.unity/blob/main/docs/USING-PLAIN-CSHARP.md)
- [KINDS.md](https://github.com/IAFahim/tl.unity/blob/main/docs/KINDS.md)

## Samples

| Sample | What it shows |
|---|---|
| Force demo | the five Force files, a cube in a SubScene, camera; Play and Edit mode tests; `Tl > Samples > Build Control Demo` adds a Burst job that starts a Dash timeline |
| Kinds IL2CPP player | the 600-step kind script and the events script in a player, both pinned hashes (needs the package tests) |
| Bullets IL2CPP player | game-code pulses summed with timeline pulses in a player (needs the package tests) |
