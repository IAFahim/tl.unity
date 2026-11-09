# Tl for Unity (`com.tl.unity`)

Timelines that run your game code: a designer places clips on a Unity Timeline, you write a few small structs and functions, and Tl plays them every tick (Burst, deterministic, rewindable, any speed).

**The full course, from first timeline to mastery, is the main README:** https://github.com/IAFahim/tl.unity#readme

## Install

Newest Unity 7 editor, project from the **Universal 3D (URP)** template. Package Manager > **+** > **Install package from git URL**:

```
https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main
```

Then Package Manager > **Tl Unity Playback** > **Samples** > **Hello Tl** > **Import**, menu **Tl Hello > Build Scene**, press **Play**.

## Also in this package

- [Glossary](Documentation~/GLOSSARY.md): the words Tl uses, in plain language.
- [Rewind and speed](Documentation~/REWIND-AND-SPEED.md): what comes back when time runs backward, and what does not.
- [CHANGELOG](CHANGELOG.md).
- Samples (Package Manager): Hello Tl, Force, Events, Crowd, Motion, Stats, Inventory, Demo hub, and two player probes (Kinds IL2CPP, Bullets IL2CPP, Scene player) that need the package tests.

MIT. See [LICENSE.md](LICENSE.md).
