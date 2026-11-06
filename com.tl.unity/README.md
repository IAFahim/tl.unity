# Tl Unity Playback (`com.tl.unity`)

Tl 4 playback-model runtime for Unity ECS with Burst: playback buffers,
library singleton, simulation systems, and generated system jobs. Compiles
against Tl.Core netstandard2.1.

## Kinds

Game-defined per-step work (value type + combine + apply) fed by clips or
pulsed by clips, markers, and game code. Full model, step order, sizing, and
reports: [docs/KINDS.md](../../docs/KINDS.md).

Samples (`Samples~`, importable from the Package Manager):

- **Kinds IL2CPP player**: Runner on kinds (priority-override boost, summed
  lifts, stat pulses) plus the 600-step script; hash `13943614770702343869`
  equals the .NET run.
- **Bullets IL2CPP player**: game-code Damage pulses + timeline hit pulses in
  one Health write; hash `3081412629212866412` equals the .NET run.

## Authoring in the Editor

Assemblies: `Tl.Unity` (runtime), `Tl.Unity.Authoring` (runtime assembly with
the authoring types; reference it from your game asmdef) and `Tl.Unity.Editor`.

Steps, each in [docs/KINDS.md](../../docs/KINDS.md):

1. `Assets > Create > Tl > Timeline Library`: point it at the folder with
   `library.json`, the timeline files and `game.tlib`, and name the ids class
   and the game assembly.
2. Add the Timeline Player component (`TimelinePlayerAuthoring`) to
   GameObjects in a SubScene. Fill Start With, May Start, Kinds and Profile by
   name. The bake gives each entity exactly the kinds it can reach.
3. Put `[RequiresKinds(typeof(Damage))]` on a component of the same
   GameObject when only game code pulses that kind.
4. Optional: `Assets > Create > Tl > Kind Profile`, and set Kind Sets on the
   library to `Union Per Profile`, so several kinds of GameObject share one
   archetype.
5. `Tools > Tl > Kind Report` logs each kind archetype per world with its
   entities, chunks, chunk capacity and kind bytes per entity, and warns about
   more than 32 kinds, more than 1 KB of kind storage, and fewer than 16
   entities per chunk.
6. `Tools > Tl > Bake Kind Capacities` runs `tlb kinds` for each library with a
   Kinds File and rewrites `Kinds.g.cs` only when a capacity changes, so
   nothing recompiles otherwise. It also runs after each script reload and when
   a `.json` in the library folder is imported. It needs a `tlb` with the
   `kinds` command; `tl.bake` 2.0.1 on NuGet is too old.
