# tl.unity

Tl timelines for Unity ECS. A designer builds timelines in Unity's Timeline window; a Playable Director in a SubScene bakes them; Burst
`ISystem`s play them on entities. A coder writes a clip struct, a kind and a track, and nothing else: no extra file, asset, tool, JSON,
generated file or asmdef.

## Install

Newest Unity 7 editor, a **Universal 3D** (URP) project, then Package Manager > **+** > **Install package from git URL**:

```
https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main
```

Import the **Force demo** sample and press Play. Or let the installer in IAFahim/tl make the whole project (URP, package, Force demo
scene with a camera, its test run):

```powershell
$env:TL_PROJECT='C:\path\MyGame'; irm https://raw.githubusercontent.com/IAFahim/tl/main/install/unity.ps1 | iex
```

```sh
curl -sSL https://raw.githubusercontent.com/IAFahim/tl/main/install/unity.sh | bash -s -- <project-path>
```

## Guides

| Guide | For |
|---|---|
| [docs/USING-ECS.md](docs/USING-ECS.md) | install, the five Force files, the designer steps, starting and pausing timelines from Burst jobs |
| [docs/UNITY-TIMELINE.md](docs/UNITY-TIMELINE.md) | how Timeline assets bake, live edit in Play, preview without Play |
| [docs/USING-PLAIN-CSHARP.md](docs/USING-PLAIN-CSHARP.md) | plain .NET hosts with an exported `game.tlib` |
| [docs/KINDS.md](docs/KINDS.md) | kinds, step order, sizing, Kind Report, presentation events |
| [docs/FORCE-DEMO-CHECK.md](docs/FORCE-DEMO-CHECK.md) | the short check in a real Editor |
| [docs/COMPARISON.md](docs/COMPARISON.md) | Tl next to BovineLabs Timeline |
| [docs/design/](docs/design/) | the zero-config plans and the results of each phase |

## Repository

- `com.tl.unity/`: the package (runtime, Timeline authoring, editor baker and preview, tests, samples).
- `external/tl-kinds`: the pinned IAFahim/tl commit the package's `Core/` and in-process packer are synced from (`eng/sync-tl-core.sh`,
  `eng/sync-tl-bake.sh`, `eng/build-generators.sh`, each with `--check`).
- `eng/test-com-tl-unity.sh`: runs the package's EditMode and PlayMode tests in a scratch project with the newest Unity 7 editor.
- `com.iafahim.tl/`, `tests/`, `external/v3`, [docs/V3-PROFILE.md](docs/V3-PROFILE.md): the earlier v1 package and the v3 profile, kept for
  their own receipts.

Agents: read [AGENTS.md](AGENTS.md) first.
