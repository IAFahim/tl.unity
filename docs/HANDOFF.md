# Handoff (2026-11-09)

Everything needed to continue is on GitHub. Nothing depends on a particular machine.

## Where things are

| What | Where |
|---|---|
| Unity package, samples, the main README (the full course) | `IAFahim/tl.unity` `main` |
| Core library, generators, packs, CLI, plain .NET demos | `IAFahim/tl` `main` |
| Field notes for agents (verified Unity facts, owner rules) | `IAFahim/unity-cli-skill` `main` |
| Hello Tl, Unity | `com.tl.unity/Samples~/HelloTl` |
| Hello Tl, plain .NET | `tl/demos/hello` |
| Crowd demo with a 55-line second host | `tl/demos/crowd` (+ `Server`) |
| Old work kept as branches (never merged) | tl: `chore/backup-dispatch-benchmark`, `feat/150-handle-interning`, `feat/155-ushort-value-pools`; tl.unity: `chore/backup-verbs-old` |

Install for users: Package Manager > Install package from git URL `https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main`, newest Unity 7 (tested 7000.0.0a16), **Universal 3D (URP)** project. The installer in `tl/install/unity.ps1` builds a whole project, imports every demo and runs each demo's test (7 `ok` lines, `PlayMode tests: passed 9`).

## How to check that main is healthy

- **tl:** `dotnet build tl.slnx -c Release -m:1 -p:NuGetAudit=false`, `dotnet test tl.slnx -c Release --no-build -p:NuGetAudit=false`, `dotnet test tests/Tl.Systems.Tests -c Release -p:NuGetAudit=false`, `python benchmarks/source_budget.py`, `python -m unittest discover -s tests`. Expected: Bake 861, Core 31, Packs 31, Gen.CSharp 47, Tlb 17, Fuzz 11, Analyzers 11, Systems 103, Python 99 OK (1 skipped), budget exit 0.
- **tl.unity:** `TL_UNITY_PACKS=1 bash eng/test-com-tl-unity.sh` and `TL_UNITY_PACKS=0 bash eng/test-com-tl-unity.sh`. The first cold run fails three known warmup tests; judge the second run. Expected: packs EditMode 82 / PlayMode 68, default EditMode 66 / PlayMode 68, 0 `error CS`, 0 `warning CS`. Also `eng/sync-tl-core.sh --check`, `eng/sync-tl-bake.sh --check`, `eng/build-generators.sh --check`, `python eng/check-force-demo-sample.py` (six `same`).
- **Bench:** the `KindGame.KindBench.RunBatch` editor method (`-kindsBench <file>`), two runs, nothing else running. A about 8 (7.5 to 8.6), B 1.8 to 2.4, B0 0.6 to 0.8 ns/entity, 0 B. A reading near 10 means another run overlapped.

## Pinned numbers (must not move without a decision)

- State hashes: `13943614770702343869`, `6607956939547305625`, `12545803591333190611`.
- Packs: Force `7683521458411619355`, Stats `12183504288633381751`, Inventory `5645869165897381019`, Motion `14881653450773647005`.
- Scenes: inventory `6806057278687917896` (1500 steps), inventory-fast `17812265086550969911` (300), inventory-mixed `15172252175353637725` (700), stats `13171269908726092120` (600), crowd `18108985316199147936` (600), hello `4684325832958264737` (150).
- The same scene hashes were produced by `tl run`, Unity (Burst), and IL2CPP and CoreCLR players.

## Decisions taken

- World rewind does **not** park at the start (changing it would move pinned hashes).
- tl.unity is public and MIT; the packs and tl are MIT.
- The shipped-tools source budget ceiling is 345000 bytes (measured 339359); the 520000 runtime ceiling is unchanged.
- `[TlState]` keeps its name. A `[TlColumn]` rename was tried and reverted because the JSON key `"states"` and `TlStateHash` would still say "state". All naming is to be decided in **one** naming review.
- No tags, releases, NuGet publishing or visibility changes.

## Open items

- **Naming review** (see the main README, chapter 13, for the list of inconsistencies).
- Unity `ITlTrack` event methods run once per step; above 1x a boundary crossed earlier in the step raises no event.
- Per-tick sums come back **one tick short** on rewind (pulses are exact). Decide whether that is intended.
- Kind `Order` across groups and assemblies, and a foreign feeder at a rate on the Unity host, are untested.
- Bug 3 of tl.unity #24 (load context not unloaded): editor hooks are now released before reload, unverified on Linux (needs the Linux Editor log).
- Real Timeline-window click check, `unity.sh` on Linux/macOS, the Linux runner (#17) are owner-only.
- Issues to close with evidence: #27, #28 (tl.unity), #579, #582 (tl).

## Things that cost hours (details in `unity-cli-skill`)

- One Unity process at a time per machine; take a lock directory with `&&`.
- `git -C external/tl-kinds checkout <sha>` of a just-pushed commit needs a `fetch` first, or it fails quietly.
- A rename is done only when every place using the word changes together; decide the full list first.
- Code shown to people reads top to bottom, names carry the story, one complete tested file (owner rule 14 in the skill).

## Videos and the 2026-11-10 follow-ups (added later)

- **Films:** `IAFahim/tl` `demos/film` makes the release demo and the Hello Tl tutorial as Tl timelines. Two renderers: HyperFrames (`release.mp4`, `tutorial.mp4`; narration from HeyGen TTS, key only in the `HEYGEN_API_KEY` environment variable, narration mp3s committed) and Unity itself (`release-unity.mp4`, `tutorial-unity.mp4`: a Unity Timeline of Tl tracks drawn with UI Toolkit; `demos/film/unity/render_film.py`). Rotate the HeyGen key that was pasted in chat.
- **Fixed in tl main:** #584 kind `Idle`, #585 `tl run --export`, #586 `tl inspect`, #587 `tl bake --emit-sources`. tl.unity runs a kind's `Idle` too (IAFahim/tl#590): its own emitters call it for every enabled kind row with no entries, and the pin `external/tl-kinds` is 50786273 (tl main 1d71cd9d plus Phase 4 spill and Phase 5b bit-scan).
- **Open:** #588 (text in clips: recommend a `TlText` id plus library string table, about 600-800 lines, not started), #589 (frame recorder as a supported tool; Unity Recorder 5.1.7 compiles on 7000.0.0a16 but stalls the DOTS world in batchmode, so frames are saved directly), #590.
- **tl source budget:** shipped-tools is 344922 of 345000 bytes. New CLI code needs a deliberate budget decision.
