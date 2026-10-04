# Portable game domain

Recoil, UI motion and typed diagnostic consumers compile against the Unity profile. The pinned tl v3 CLI generates their bindings before compilation. Install the resulting `Tl.Sample.Game.Domain` package through NuGetForUnity; no manual consumer registration is required.

Change the ordinary track/clip structs and pure `Fold` / live `ExecuteActive` methods, build the project, install its new package version, and rebake JSON against the corresponding game assembly. The game chooses how its ECS components map to these columns and how clocks advance.
