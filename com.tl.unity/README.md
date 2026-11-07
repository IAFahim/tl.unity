# Tl for Unity (`com.tl.unity`)

Timelines for Unity ECS with Burst: clips on tracks, several timelines playing
on one entity, exact rewind. Designers author json (or draw in Unity's
Timeline window); a baker packs it into one `game.tlib`; a Burst `ISystem`
step plays it on unmanaged data.

Tested on the newest Unity 7 editor (tested on 7000.0.0a16).

## Quick start

One command (needs git, the .NET 10 SDK, the Unity CLI) makes a Unity 7 project with Tl ready for game code. Safe to rerun:
```sh
curl -fsSL https://raw.githubusercontent.com/IAFahim/tl.unity/main/eng/tl-init.sh | bash -s -- <new-project-path>
```

## Install

`Packages/manifest.json`:

```json
"com.tl.unity": "https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"
```

You also need `tlb` with the `kinds` command, built from IAFahim/tl `main`
with the .NET 10 SDK (`git clone --depth 1 https://github.com/IAFahim/tl.git`,
then `dotnet build tools/Tl.Bake -c Release`). Set the Tlb field of the
Timeline Library asset to the `Tl.Bake.dll` it produces.

The generators ship in the package (`Generators/Ecs`, `Generators/Plain`).
Add `Tl.Unity.Generate` (ECS) or `Tl.Unity.Generate.Plain` (plain C#) to the
references of your game asmdef. Unity then generates the glue code on every
compile; you run no tool for it.

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
- `Tl.Unity.Generate`: opt-in marker for the source generator. Reference it
  from the asmdef that declares your kinds and `[TimelineSystem]` structs.
- `Unity.Tl.CodeGen` (Editor, IL post-processor): registers the generated
  components with Entities when such an assembly loads.
- `Tl.Core`: the shared timeline core, copied from IAFahim/tl.

## Known gaps

- The Linux CI runner is not started:
  [#17](https://github.com/IAFahim/tl.unity/issues/17).

See "Known gaps" in [docs/USING-ECS.md](../docs/USING-ECS.md#10-known-gaps).

## Samples

`Samples~` has two headless IL2CPP receipts, Kinds and Bullets, each with a
state hash that equals the .NET run. They run the package test game, so they
need `"testables": ["com.tl.unity"]`. Details in
[docs/KINDS.md](../docs/KINDS.md).
