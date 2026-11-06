# Repository rules

These rules apply to every human and automated contributor.

## Start here

- Before any work, pull the skill repo (`git -C <clone of https://github.com/IAFahim/unity-cli-skill> pull --ff-only`, clone it if missing) and read `skills/unity-cli-field-notes/SKILL.md`. Before you finish, push what you learned back to its `main`. Every agent brief starts with this.
- Use only the newest Unity 7 editor. Install a newer one by name with the Unity CLI (`unity install 7 --yes --accept-eula`); never test, pin or mention older versions.
- Finish and test locally first. CI is not a priority.
- Direct pushes to `main` are authorized by the owner: `git pull --rebase origin main`, then `git push origin HEAD:main`, never `--force`.
- Never hand-write or hand-edit Unity-serialized files (`.asset`, `.unity`, `.prefab`, `.mat`, `.meta`, ProjectSettings). Create them through Unity itself; the skill has the recipe.
- Unity ECS: `ISystem`, `IJobEntity`/`IJobChunk`, Burst, unmanaged data only; never `SystemBase`, managed components or GameObject lookups. A needed workaround means a lib is missing something: stop and `gh issue create` in that repo.
- Designers wire references in the Inspector; no `AssetDatabase.Load*`, `Resources.Load` or Find-by-name in runtime or authoring flow.
- No code comments.

