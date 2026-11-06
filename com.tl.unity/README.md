# Tl for Unity (`com.tl.unity`)

Timelines for Unity ECS with Burst: clips on tracks, several timelines playing
on one entity, exact rewind. Designers author json (or draw in Unity's
Timeline window); a baker packs it into one `game.tlib`; a Burst `ISystem`
step plays it on unmanaged data.

Tested on the newest Unity 7 editor (tested on 7000.0.0a16).

## Install

`Packages/manifest.json`:

```json
"com.tl.unity": "https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"
```

You also need `tlb` with the `kinds` command, built from IAFahim/tl `main`:
`dotnet build tools/Tl.Bake -c Release`. Set the Tlb field of the Timeline
Library asset to the `Tl.Bake.dll` it produces.

## Start here

| I want to | Read |
|---|---|
| Go from an empty project to a running demo on ECS | [docs/USING-ECS.md](../docs/USING-ECS.md) |
| Play timelines from a plain `MonoBehaviour`, no ECS | [docs/USING-PLAIN-CSHARP.md](../docs/USING-PLAIN-CSHARP.md) |
| Draw timelines in the Unity Timeline window | [docs/TIMELINE-BRIDGE.md](../docs/TIMELINE-BRIDGE.md) |
| Compare Tl with the BovineLabs timeline | [docs/COMPARISON.md](../docs/COMPARISON.md) |
| Look up the kind model, sizing, capacity bake and Kind Report | [docs/KINDS.md](../docs/KINDS.md) |

## What is in the package

- `Tl.Unity` (runtime): playback buffers, the library and step singletons,
  the Burst `ISystem` step (`TimelineRequestSystem`, `TimelineAdvanceSystem`,
  `TimelineKindTickSystem`, `TimelineEventSystem`), pulses, the Kind Report
  logic.
- `Tl.Unity.Authoring` (runtime): Timeline Player, Timeline Library, Kind
  Profile and `[RequiresKinds]`. Reference it from your game asmdef.
- `Tl.Unity.Editor`: the baker, `Tools > Tl > Kind Report` and
  `Tools > Tl > Bake Kind Capacities`.
- `Tl.Core`: the shared timeline core, copied from IAFahim/tl.

## Known gaps

The generated entity code is not produced by Unity yet, and `Events<T>` is not
dispatched on Unity. See "Known gaps" in
[docs/USING-ECS.md](../docs/USING-ECS.md#10-known-gaps): issues
[#11](https://github.com/IAFahim/tl.unity/issues/11),
[#12](https://github.com/IAFahim/tl.unity/issues/12) and
[#13](https://github.com/IAFahim/tl.unity/issues/13).

## Samples

`Samples~` has two headless IL2CPP receipts, Kinds and Bullets, each with a
state hash that equals the .NET run. They run the package test game, so they
need `"testables": ["com.tl.unity"]`. Details in
[docs/KINDS.md](../docs/KINDS.md).
