# Many At Once

Chapter 7 of the main README as a scene: a platform that plays two timelines at once, two tracks that add, and two clips on one track that crossfade.

1. Menu **Tl Many At Once > Build Scene**. It builds `Scenes/Ride.playable`, `Scenes/Shake.playable`, a Platform in a SubScene and a camera, and opens `Many At Once.unity`. It never overwrites a timeline you edited; delete one to get the original back.
2. Press **Play**. The platform shakes, bobs and rides up from 1 m to 5 m. The Console prints a line every 15 ticks of the Ride timeline.

What plays:

| Timeline | Track | Clip | Time | Height |
|---|---|---|---|---|
| Ride (the Playable) | Floor | Ground | 0 to 2 s | 1 |
| Ride | Floor | Top | 1.5 to 3 s | 5 |
| Ride | Wave | Wave | 0.5 to 1 s | 0.5 |
| Shake (Also Plays, Play On Awake) | Bump | Bump | 0.25 to 0.5 s | 0.25 |

Files: `Scripts/Platform.cs` (all of Tl's part), `Scripts/PlatformLiftSystem.cs` (your game code), `Scripts/PlatformLogSystem.cs` (the Console lines), `Editor/ManyAtOnceBuilder.cs` (the menu), `Tests/ManyAtOnceTests.cs` (a PlayMode test that checks the height on every tick it sees).

Things to try in the Timeline window and the Playable Director's **Tl** foldout:

- Untick **Play On Awake** next to Shake in **Also Plays**: the bump at 0.25 s is gone.
- Give Ride and Shake the same **Channel**: Shake replaces Ride when it starts.
- Remove `: IBlend<HeightClip>` and the `Blend` method from `HeightClip`: during the overlap only Ground counts, then the platform jumps to 5.
