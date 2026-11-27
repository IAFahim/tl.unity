# Deterministic frame recorder

`TlFrameRecorder` (`Tl.Unity.Editor`) records a timeline-driven scene deterministically by locking `Time.captureFramerate` to `TlProjectSettings.instance.TicksPerSecond` (60 by default), so every rendered frame advances by one Tl tick.

## Menu entry

- **Tools > Tl > Record Frames**: opens Play mode on the currently open scene (preserving the request in `SessionState` across domain reloads), records 150 frames capturing every 2nd frame at `1280 x 720` into `Logs/TlFrames/<scene name>`, resets `Time.captureFramerate` to `0`, and exits Play mode when finished.

## Command line

Run without `-quit` so Unity ticks Play mode through to completion and exits cleanly with code `0` (or `1` on invalid arguments, missing scene, or truncated capture):

```bash
Unity -batchmode -projectPath <project> -executeMethod Tl.Unity.Editor.TlFrameRecorder.RecordFromCommandLine \
  -tlRecordScene <Assets/Path/Scene.unity> \
  -tlRecordFrames 240 \
  [-tlRecordEvery 2] \
  [-tlRecordOut Logs/TlFrames/MyScene] \
  [-tlRecordWidth 1280] \
  [-tlRecordHeight 720]
```

## Public API & settings

`TlFrameRecorder.Start(TlFrameRecorder.Settings settings)` (or `TlFrameRecorder.RecordInPlayMode(...)` inside `[UnityTest]` coroutines):

| Setting | Default | Meaning |
|---|---|---|
| `ScenePath` | currently open scene | Scene asset path to open before entering Play mode |
| `Frames` | `150` | Total simulation frames/ticks to capture |
| `Every` | `2` | Write output every Nth frame |
| `Width` | `1280` | RenderTexture width in pixels |
| `Height` | `720` | RenderTexture height in pixels |
| `OutputFolder` | `Logs/TlFrames/<scene name>` | Existing files in this folder are cleared before writing |
| `Camera` | `Camera.main` | Camera rendered for PNG frames |
| `TimeoutSeconds` | `150` | Wall-clock deadline waiting for the first active playback and remaining frames |

## Output format

- `ticks.csv`: header `frame,tick,x,y,z` using invariant culture and 4 decimal places (`0.0000`), sampled from the first playing `Playback` slot (excluding `StartPending`) and its entity's `LocalTransform`.
- `tick_XXXX.png`: zero-padded tick index (`tick_0030.png`, `tick_0090.png`, ...), named by Tl tick so tests can compare frames by tick.

## Graphics device requirement (`-nographics`)

PNG pictures require a real graphics device. When Unity runs with `-nographics` (`SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null`), `TlFrameRecorder` logs one warning, skips PNG creation, and writes `ticks.csv` only. On headless Linux servers, wrap the command in `xvfb-run` (with a renderable backend such as `-force-vulkan`) to produce pictures.
