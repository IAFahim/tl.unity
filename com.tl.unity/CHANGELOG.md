# Changelog

Newest first. Taken from the commit history since the zero-config plan started (`cbb7f14`). Short hashes name the commit.

## Unreleased

### Added
- Host-parity fixture (IAFahim/tl#603, #604 U10): `Tests/Parity` carries tl's two-assembly domain (an `Idle` kind fed from the other assembly, an `Idle` kind fed in its own assembly, a kind without `Idle`), library, request script and expected column bytes per tick, copied by `eng/sync-parity.sh` (checked by the gate script); the PlayMode test replays it, 600 ticks with two world rewinds, and compares the column bytes every tick with the .NET host's.
- `TimelineRequest.Reset(slot)` for `RequestOp.Reset` (tl Phase 8b); the Unity generators carry tl's derived kinds (`Sum<T>`, `Set<T>`, `While<T>`) and the TLGEN113 and TLGEN114 warnings. The `external/tl-kinds` pin moved to the tl Phase 8 branch.
- A kind may declare `public static void Idle(...)`: it runs once per tick for every enabled kind row with no entries, so a column falls back (a "can double jump" flag goes back to false when its clip ends), the same rule as the .NET host (IAFahim/tl#584, #590). The `external/tl-kinds` pin moved to tl `434d729`.
- Hello Tl sample (the smallest complete program) and a full course in the main README, from the first timeline to baking, rewind, speed, outside Unity and project ideas.
- Tl Panel: a component with Play, Pause, Resume, Stop, Reverse, Seek, speed 0.25x to 4x, Stop channel, Stop all and a world rewind switch; the Force demo scene has one and `GameObject > Tl > Panel` adds it to any scene (`10902c9`).
- Channel names: Project Settings > Tl names the 15 channels; the Timeline Channel menu, `Tl > Export` and the packer use them (`3485880`).
- `Tl > Export Scene JSON` writes the timelines, `tl.project.json` and a scene for the selected objects; a scene is plain bytes that run in any player (`TlSceneData`, `TlSceneRun`); the Scene player sample runs the Inventory and Stats scenes in IL2CPP and CoreCLR players (`3485880`).
- Speed per playback and per channel from Burst jobs: `TlRequests.SetSpeed`, `SetChannelSpeed` (`cae822a`, `04f89ff`); seek, reverse, channel pause, resume, seek and reverse (`01e19a8`).
- Pack adapters, `Tl` menu entries for pack clips and the Motion and Stats demos (`968ce7c`); the Inventory demo (`6081d2a`); the Crowd, Events and Demo hub samples (`87c7129`, `42cbebc`, `14bacb0`).
- Tl markers (`IMarker<T>`) authored in the Timeline window (`61336db`).
- Portable JSON export (`Tl > Export`) (`384949d`); `TlSceneRunner` runs scene JSON in a Unity world (`09693d7`).
- Force demo: there and back with Return, follow camera, grid floor, readout (`32d3db4`).

### Changed
- The `external/tl-kinds` pin moved to tl `79c4cbcf` (tl main plus the per-entity spill run, the bit-scan and the dense dispatch): Core and the bake tools resynced, the Unity generators read the system declaration the way tl now does (no rewritten compilation, `TlFrame` read from the parameter type). The request sort in `TimelineRequestSystem` and the event sort in generated jobs are now Burst-safe code in tl.unity, because tl's new sorts call `NativeMemory.AlignedAlloc`, which Burst cannot compile (it disabled Burst for every job in the assembly).
- The coder API (`TlClip`, `TlKind`, `ITlTrack`, `TlFrame`, `TlCurve`, `TlAssetId`, `TlTimeline`) comes from Tl.Core (`916a874`).
- Kinds are identified by full type name (`384949d`); pair keys no longer contain the assembly name (`1a375ff`).
- One set of rules shared with the .NET host: requests (`a309cfd`), tick advance (`2b430f5`), kind entries (`086d561`), live swap (`33c76a8`), and one library assembler (`ca76974`).
- Each auto kind adds only the columns its Apply and feeders use (`64a2be7`).
- Clip mirrors and menu stubs carry the clip's namespace (`59ac1e5`).
- The editor releases its static event hooks before an assembly reload (`3485880`).

### Removed
- The legacy library, bridge and tool path (`7f3a0ee`).

### Fixed
- `Events<T>` raised in `OnClipStart` of an `ITlTrack` reach `TimelineEvent<T>` (`d582eb3`).
- Real `_TlMenu` stub files and no red error for empty tracks (`be6d0b3`).
- Edit-mode preview keeps the last tick's state when a timeline hands over to the next (`b841389`).
- A reversed playback parks at its start and plays again when it faces forward (`f9bd9d9`).

## 0.2.0-preview.1

Zero-config path: Tl tracks, the director baker and Entities Graphics (`cf6c3a3`); the Force demo (`3d56728`); live edit in Play, Edit-mode preview, the Tl Inspector section (`012438c`); `TlTimeline` fields and Burst `TlRequests` with Pause and Resume (`dc224ad`); IL2CPP and CoreCLR players print both pinned hashes (`37003c7`).
