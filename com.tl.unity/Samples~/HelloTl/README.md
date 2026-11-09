# Hello Tl

The smallest complete Tl program: a player that teleports along X on a timeline. Start here, then read the main README (chapter 1).

1. Menu **Tl Hello > Build Scene**. It builds the timeline, a Player cube in a SubScene and a camera, and opens `Hello.unity`.
2. Press **Play**. The top left shows the player's x and the timeline tick. The Console prints a line each time x changes:

```
Hello Tl: the player is at x = 5 (timeline tick 30)
Hello Tl: the player is at x = 13 (timeline tick 90)
Hello Tl: the player is at x = 5 (timeline tick 119)
```

Files: `Teleport.cs` (all of Tl's part), `PlayerTeleportSystem.cs` (your game code), `PlayerReadout.cs` (on-screen text), `Editor/HelloTlBuilder.cs` (the menu).
**Tl Hello > Export Library** writes `HelloTl.tlib` in the project folder, which a plain .NET program can run (main README, chapter 9).

The plain .NET version of the same program (no Unity) is https://github.com/IAFahim/tl/tree/main/demos/hello .
