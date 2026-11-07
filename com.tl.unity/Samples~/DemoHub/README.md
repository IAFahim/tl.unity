# Demo hub

One scene, **Tl Demos**, with a button per demo scene. A **Tl Demos** button in the top-right corner of every demo brings you back.

1. Import the demos you want (Force, Events, Crowd) and let each one build its scene.
2. Import **Demo hub**, then `Tl > Samples > Build Demo Hub`. It finds every scene under `Assets/` whose name ends in ` Demo`, writes
   `Scenes/Tl Demos.unity` and sets the build list to the hub first, then the demos.
3. Open **Tl Demos**, press Play, click a demo.

The installer in IAFahim/tl (`install/unity.ps1`, `install/unity.sh`) does all of this and runs every demo's test.
