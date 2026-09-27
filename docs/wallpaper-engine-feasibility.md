# Wallpaper Engine: common rendered-surface capture

Evidence collected on the available Windows desktop on 2026-09-27. The integration targets the **actual Windows desktop host containing Wallpaper Engine's rendered output**, with the selected monitor as its persisted identity. It does not select a Chromium class, open another player, replay a video asset or change the wallpaper.

## Architecture and source behavior

The source type `WallpaperEngine` is appended as value 5, preserving existing saved source values. The add/edit dialog selects a monitor, checks whether an eligible live surface exists, and explains the producer's playback boundary. Monitor selection survives clone, undo, scene storage and scene import/export. Existing source availability retries also apply when Wallpaper Engine is started later.

The common capture target is the `WorkerW` desktop branch containing verified Wallpaper Engine process windows. Scene, video and web content all pass through this host-level path: renderer-specific browser handles are not the integration boundary. A source must report an unavailable host rather than silently capture foreground applications or substitute a different player.

Two mechanisms were investigated:

- **Windows Graphics Capture:** works for the top-level `Progman` host on this machine. Native `CreateForWindow` rejects the child `WorkerW` with `E_INVALIDARG` (`0x80070057`), independently of ScreenRecorderLib. Other Windows desktop arrangements may expose an eligible top-level wallpaper host.
- **`PrintWindow` on the wallpaper `WorkerW`:** returns the live composed wallpaper pixels here, including while the entire host is covered. This is the common-host fallback used by the isolated capture worker. It avoids depending on internal renderer classes.

The host needs to be selected carefully. Capturing the whole `Progman` indiscriminately could include desktop icons; cropping it to the monitor cannot remove icons within that monitor. Wallpaper capture should select the verified wallpaper-only `WorkerW`, excluding any branch containing `SHELLDLL_DefView` / `SysListView32`. A fallback to desktop/display capture would violate the source's meaning.

The implemented service uses a hidden child process of the application for `PrintWindow`, crops the host to the selected physical monitor and transfers bounded BGRA frames to the existing JPEG pipeline. Stop, timeout and failed discovery terminate this owned process and clear its last published frame. The release executable's real worker entry point was exercised directly: three 319 × 199 frames, cancellation cleanup, and rejection of malformed arguments without launching the application UI. Automated tests also cover a blocked native helper, cancellation, negative monitor offsets, odd output dimensions and missing displays.

## Native host evidence

The observed tree was:

```text
Progman (4880 × 2560, top level)
├── SHELLDLL_DefView
│   └── SysListView32 (currently invisible)
└── WorkerW (4880 × 2560)
    └── Wallpaper Engine rendered window subtree
```

The wallpaper and icon branches are siblings. The wallpaper host and its source occupy `(0, -494, 4880, 2560)` in desktop coordinates, illustrating why monitor cropping must use physical geometry and preserve negative offsets.

The common-host run measured the following. All covered phases passed the cover geometry, z-order and unchanged-foreground checks at every sample.

| Target / mechanism | Baseline | Fully covered by test window |
| --- | --- | --- |
| `WorkerW` / WGC native factory | `0x80070057`, no capture item | Not attempted after rejection |
| `WorkerW` / `PrintWindow` | 12 frames; 12 distinct hashes; 9 after warmup | 12 frames; 12 distinct hashes; 9 after warmup |
| `WorkerW` / native read cost | median 66.3 ms; maximum 76.0 ms | median 70.5 ms; maximum 82.4 ms |
| `Progman` / WGC | 20 frames; 20 distinct hashes; 15 after warmup | 20 frames; 20 distinct hashes; 15 after warmup |
| `Progman` / `PrintWindow` | 12 frames; 12 distinct hashes; median 66.1 ms | 12 frames; 12 distinct hashes; median 72.2 ms |

Both hosts returned more than 99.99% nonblack samples with thousands of sampled colors. Unlike a single screenshot, continuing hash changes after excluding startup samples establish that fresh pixels were observed while the host remained fully covered. `Progman` WGC is a useful efficient candidate, but this machine's icon list was invisible, so that run **does not establish exclusion of visible desktop icons**. The `WorkerW` branch has the correct structural isolation.

At this native desktop size, the measured `PrintWindow` read alone does not support a sustained 30 FPS promise. A worker must pace reads, bound frame memory and avoid queueing captures faster than the previous call can finish. These numbers are a local full-resolution measurement, not a universal benchmark.

## Renderer coverage matrix

| Renderer | Integration path | Direct live validation in this session |
| --- | --- | --- |
| Web | Common wallpaper `WorkerW`, without matching Chromium classes | Production service: 6 / 6 distinct JPEGs under complete local occlusion |
| Scene | Same common wallpaper host | Production service: baseline and covered phases each 6 / 6 distinct JPEGs |
| Video | Same common wallpaper host | Production service: baseline and covered phases each 6 / 6 distinct JPEGs |

The architecture is intentionally renderer-independent. This matrix distinguishes that implementation scope from the live combinations actually tested. These live tests do not certify every Windows build, renderer version, GPU driver, protected surface or producer state.

The scene and video tests temporarily selected installed wallpapers through the supported `openWallpaper` command: the built-in Beach scene and a silent video whose MP4 metadata contains no audio track. They captured Wallpaper Engine's actual desktop output, without a duplicate player. A separate 180-second watchdog and a `finally` block restored the original web wallpaper. Both display-location queries then returned its original path; the complete saved selection map, layout, global settings and original wallpaper property entry compared equal before and after. No configuration file was overwritten and no pause, playback or audio setting was changed. Reloading necessarily restarts a wallpaper's runtime/playback position and can update its recent-item history. [Official command-line interface](https://help.wallpaperengine.io/en/functionality/cli.html).

At 319 × 199, sampled colors were 505 baseline / 500 covered for the scene, and 386 / 387 for the video. Each run also verified a stop/restart at 3 × 5, exact JPEG dimensions, removal of the child process and cached frame, and explicit failure for a missing display. One initial scene interval was rejected because foreground changed during sampling; its repeat passed with unchanged foreground and valid cover geometry throughout. The production test now proves nonactivation during cover setup and independently checks coverage/z-order for every frame, so ordinary user focus changes cannot invalidate otherwise established occlusion.

The earlier investigation of private renderer descendants remains diagnostic background: five child windows rejected native WGC; four returned black through `PrintWindow`, while `Intermediate D3D Window` returned changing pixels. **Those private children are not the generic source's target.** The later `WorkerW` result supersedes the browser-specific route.

## Occlusion method and producer boundary

The probe creates an opaque black top-level test window with `NOACTIVATE`, `TOOLWINDOW` and disabled input. It inserts this owned window immediately above the desktop root while preserving ordinary applications above it. It never reparents Wallpaper Engine windows or alters their hierarchy. It checks complete rectangle coverage, immediate z-order adjacency and unchanged foreground before and after every sample. The WGC covered session starts after the cover has been presented and settled. Cleanup destroys the test window in `finally`; all cleanup checks passed.

This is complete local visual occlusion of the rendered output. It does not trigger every policy associated with another application becoming fullscreen, desktop switching, producer suspension or a disconnected monitor. Wallpaper Engine itself documents game-related pause and stop behavior in its performance settings. A capture consumer can keep polling, but cannot create newly animated frames if the producer has stopped rendering. The application does not override those settings or claim that repeated identical pixels prove continuing animation. [Official Wallpaper Engine performance guidance](https://help.wallpaperengine.io/en/performance/game.html).

Microsoft documents `PrintWindow` as a synchronous call handled by the target application. Both native recording and print experiments therefore run in helper processes with an 18-second test deadline. A timeout terminates only the probe helper and removes its owned windows; an in-process `Task.Run` timeout would leave a blocked native call alive. [Microsoft `PrintWindow` documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow).

Native factory checks use `IGraphicsCaptureItemInterop::CreateForWindow` directly, separating Windows capture-item rejection from recorder/encoder errors. They verified `S_OK` for `Progman` and `E_INVALIDARG` for `WorkerW` and every tested renderer child. [Microsoft `CreateForWindow` documentation](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow).

## Reproduce

Inventory including common desktop hosts, without capturing pixels:

```powershell
dotnet run --project tests/BetterSignalRGB.WallpaperProbe -c Release -- --desktop-hosts
```

Native capture-item eligibility only:

```powershell
dotnet run --project tests/BetterSignalRGB.WallpaperProbe -c Release -- --desktop-hosts --hosts-only --capture-wallpaper --factory-only
```

Temporal common-host capture:

```powershell
dotnet run --project tests/BetterSignalRGB.WallpaperProbe -c Release -- --desktop-hosts --hosts-only --capture-wallpaper --print-window --temporal
```

The following additionally covers the wallpaper briefly, behind ordinary applications, preserving foreground and removing the owned cover afterward:

```powershell
dotnet run --project tests/BetterSignalRGB.WallpaperProbe -c Release -- --desktop-hosts --hosts-only --capture-wallpaper --print-window --temporal --occlude
```

The probe retains pixels only in memory and emits aggregate dimensions, timings, color counts and counts of distinct RGB hashes. It saves/transmits no images or individual hashes. It is intentionally excluded from ordinary CI.

Production capture, full desktop occlusion, stop/restart and failure cleanup for the currently running wallpaper:

```powershell
dotnet run --project tests/BetterSignalRGB.NativeSmokeTests -c Debug -p:Platform=x64 -- --capture-wallpaper
```

This command does not select or change a wallpaper. The cross-renderer results above required a separately supervised, bounded switch/restore test; reproducing them requires selecting each renderer in Wallpaper Engine before running the same production test.
