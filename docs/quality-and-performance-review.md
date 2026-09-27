# Capture and canvas overhaul

This change repairs the capture, editing, composition and delivery paths together. It preserves the saved source IDs and the 320 × 200 logical canvas. The web/editor output can render at 800 × 600 in HQ mode. Device resolutions, encoder surfaces and output resolution are separate from logical scene coordinates.

## Problems fixed

| Area | Previous behavior | New behavior |
| --- | --- | --- |
| Native frames | A native bitmap pointer could be used after its recording callback returned. | Copy valid pixel rows during the callback into pooled memory; JPEG work runs on a background worker. |
| Frame backlog | UI dispatch, encodes and SignalRGB requests could accumulate independently. | One pending native frame per source; slow consumers skip obsolete frames. SignalRGB has one serialized sender. |
| Recording memory | Unused video accumulated in a `MemoryStream`. | The native video carrier uses a seekable discard stream with no retained video bytes. |
| Resolution failures | Minimum sizes were imposed on sources and individual region tiles. | Bounded JPEG dimensions, a separate native encoder carrier and one software retry for recognized hardware encoder failures. |
| Region capture | Narrow monitor intersections were independently enlarged, causing overlaps/seams. | Intersections retain their relative positions in one pixel coordinate system. DPI conversion uses the same rounded shared edges. |
| Canvas controls | Collection changes rebuilt the whole canvas and its WebViews; navigation repeated subscriptions and could block synchronously. | Controls are reconciled by identity, collection updates are coalesced and subscriptions have an explicit lifecycle. |
| Preview | Every source frame could create and queue new UI decoding work. | Preview dispatch is bounded, and the bitmap object is reused. |
| Geometry | Several operations still assumed an 800 × 600 canvas; resize, crop and mirror calculations disagreed across outputs. | Shared geometry helpers; rotated resize anchors, aspect ratio locking, atomic crop changes and matching clip polygons. |
| History | JSON history lost `RectInt32` fields; some commands recorded state after the edit. | Detached source snapshots before edits, bounded to 50 states, preserving region coordinates and source identity. |
| Saving | Concurrent settings writes could lose keys or leave invalid files; frequent gestures wrote repeatedly. | Serialized settings access, detached snapshots, a short save debounce and atomic file replacement. |
| Recording state | Adding/pasting/undoing could start capture while recording was stopped; auto-start raced loading. | Initialization is awaited; source starts and recording transitions are coordinated; pause/stop prevent late starts. |
| Composition | Repeated complete decoding/compositing, missing transforms and off-canvas rectangle shrinking. | Decode each changed source once, reuse the surface, clip instead of shrink, apply opacity/crop/mirror/rotation and render only for consumers. |
| Streams | Repeated identical frames, idle/disconnected clients retained, broken multipart framing and inconsistent web canvas dimensions. | Latest-frame broadcasts, cancellation/timeouts, shared multipart writer and shared web renderer; `/stream` supplies the composite. |
| SignalRGB effect | The fullscreen halo recomputed a different per-source layout; combined source/crop rotations and mirrors disagreed with the canvas. Some filters replaced each other. | One Canvas2D scene shared by picture and halo, matching the production compositor; cached paths, bounded decoding and composed color/blur filters. |
| Effect controls | Appearance was configured only in SignalRGB. | Optional versioned app controls for picture, color, halo, interpolation and 1–30 FPS delivery; disabling the override restores host appearance controls. |
| Websites | Navigation state was read off the UI thread; viewport settings were not respected; CSS zoom was applied twice. | State read before closing, native viewport dimensions, one zoom application, direct JPEG capture and bounded downstream processing. |

Capture errors are displayed in the application instead of being available only in debug output. The recording frame rate still controls capture cadence. Resizing a source marks the capture configuration for refresh, while layout-only changes are applied to streaming without restarting the recorder.

Automatic recording defaults to enabled, including when the preference is absent in older settings. An explicit saved `false` remains disabled. Startup awaits source initialization; website capture creates its own browser host without waiting for editor controls. The shared preference reader is covered through real settings serialization and reopening.

Three native WinUI crashes were diagnosed and corrected: overlay Z-order values exceeded WinUI's supported maximum, a custom setup control received a `SettingsCard` style from `SettingsExpander`, and the disabled recording button animated `Foreground` on a `Border`. The last failure was captured in the actual application log at 2026-09-27 00:18:09 UTC (`Cannot resolve TargetProperty Foreground on specified object`). Overlay values now stay within the platform limit, setup content is hosted in an actual settings card, and text animation targets the real `ContentPresenter`. Production XAML contract checks cover these failures, including template-local storyboard target resolution. Recoverable async editor/tray actions report errors; fatal startup errors retain their exception and are logged before propagation. Bounded local logs preserve original managed stacks, including async startup failures that may otherwise surface only as native stowed exceptions.

Window selection keeps the exact HWND, owning process, executable path and title. Recovery after restart requires an unambiguous match. Monitor/camera identities no longer silently fall back to a different identically named device. Webcams expose Auto and the device's supported capture formats; missing saved formats produce a visible error. Source edits stop the previous capture before changing its settings and respect concurrent stop/pause intent. Region setup uses the same monitor mapping as live capture and completion-driven, bounded PNG previews in memory.

## Canvas interaction model

Source, group, crop and preview lifecycles have separate modules. Shared helpers calculate the visible polygon, transforms, layer order, pointer ownership and candidate geometry independently of XAML. The view model no longer holds references to `DraggableSourceItem`. Each website capture session owns an independent browser host, and every canvas source displays the production JPEG stream. The canvas contains no second website browser and can unload without destroying capture. Stop/pause retains the last displayed image; sources that have never captured show a placeholder. The add/edit dialog retains its separate interactive website setup preview.

- Pointer and marquee selection use the crop/source polygon intersection, followed by source rotation. Empty corners of a rotated bounding box do not count as content.
- Resize handles refer to the visible frame. The opposite visible anchor is preserved, and large pointer jumps are constrained instead of rejecting the entire movement. Free resizing at cardinal angles constrains X and Y independently, so hitting one canvas edge cannot shrink the other dimension. Continuous constraints precede integer quantization, including group children, to avoid half-pixel validity gaps at 90 degrees.
- Source rotation, including numeric edits, pivots around the visible frame center. Escape restores an in-progress gesture; clicking a handle without changing geometry does not add an undo state.
- Rotation knobs can extend beyond the canvas edge into the editor margin. Source, group and crop knobs retain their offset from the visible frame; only the actual content is constrained to the canvas.
- Rotated crop masks and groups containing children angled relative to the group use uniform resizing where an anisotropic transform would require shear that the source model cannot represent.
- Cropping edits the mask independently of the image. Accept, cancel, pointer cancellation, source replacement and navigation have explicit paths. Cancelling persists the restored crop so a temporary edit cannot reappear after restart.
- Mirroring now affects the content inside each source, retaining its crop, rotation and placement. This intentionally replaces the former combination of content reflection, group repositioning and per-source boundary correction.
- Arrow keys nudge by one logical pixel, Shift by ten, while preserving spacing within a group and respecting visible bounds. Text fields keep their own keyboard shortcuts.
- Layer commands retain the relative order of a selection at the front/back and when stepping through adjacent layers.
- Selection synchronization and control reconciliation are coalesced. Bulk selection does not repeatedly recalculate every bound property and command per item.
- Async source mutations have an editing state. Undo cannot restore the canvas midway through an unfinished paste and then leave that paste adding more objects.
- Rejected or rounded numeric edits display the accepted geometry and do not erase redo history when no model value changes.
- Layout locks persist through history and scene storage. Any locked member blocks a selection transform; copying and deleting remain explicit actions, and pasted copies start unlocked.
- Snapping compares visible edges and centers against other sources and the canvas within six screen pixels. Pointer corrections preserve resize anchors, Alt bypasses snapping, and guides only show alignments reached after final boundary constraints.

The canvas header includes a controls help flyout. Saved source IDs and crop percentages remain compatible with the existing format.

## Clean web output and high quality mode

Both `/` and `/canvas` are clean output surfaces for browser effects: no visible text, debug status, controls or scrollbars. They consume the production `/stream` composite, with one active JPEG decode and only the latest pending frame. A bounded multipart reader reconnects after EOF/errors and releases decoded bitmaps. The canvas fills the browser viewport and updates its actual pixel dimensions when the stream changes resolution.

The optional **HQ 800 × 600** mode raises capture detail and renders the web composite at 800 × 600 with JPEG quality 92 and bicubic interpolation. It does not upscale an already composed 320 × 200 frame. SignalRGB retains its original source dimensions and JPEG quality 65, using a separately cached frame from the same capture. Its update cadence and layout protocol are unchanged. Normal mode shares the original JPEG between both outputs instead of doing redundant encoding.

Source geometry stays canonical: the editor Viewbox and final composite transform scale the whole scene, including rotated masks, from 320 × 200 to 800 × 600. This preserves relative placement without accumulating integer rounding errors when toggling modes. Saved scenes, undo geometry, locks and SignalRGB layouts do not change. HQ is a separate global preference, off by default. Capture sessions restart under their lifecycle gate when quality changes; the HTTP/HTTPS servers remain connected, and a concurrent stop/pause/shutdown takes precedence over restarting sources. Snapping accounts for both output scale axes and zoom.

Regression checks verify actual 800 × 600 encoded pixels, high-frequency detail beyond a low-resolution intermediate, combined rotations/crops, live HTTP and HTTPS resolution changes on an existing connection, and SignalRGB's unchanged frame size. Browser checks read actual canvas pixels across a live HQ switch, a switch back, viewport resizes and stream reconnection. View-model checks cover repeated lossless toggles, saved preference loading, persistence/capture configuration failures, running and paused sources, and a concurrent stop.

## SignalRGB rendering and controls

The included effect no longer builds nearest-source ownership regions for fullscreen ambilight. It draws the same transformed source scene once and uses it for both picture and halo. Color presets, hue, brightness, saturation and image blur combine in one filter chain. Full-area halo also honors those controls when the picture is hidden. See [rendering validation](signalrgb-effect-validation.md) for the production-compositor comparison and independent pixel oracle.

The effect keeps one displayed image, one active decode and only the newest pending image per source. A global scheduler permits two decoders at once; retained encoded data and decoded pixels have separate bounds. Identical images and settings do not trigger new decoding/rasterization. The app sender repeats a paused source at most once per two seconds so reloading the effect can recover it without resuming capture.

App appearance control is opt-in under Settings → SignalRGB effect. Appearance updates and source transactions share the same serialized sender. The matching HTML effect is copied beside the application during build and publish. Its controls retain the host's original settings so app control can be disabled without losing them.

The setup panel detects current and legacy effects folders, compares SHA-256 revisions and installs the matching bundled file only on explicit user action. Replacing an existing effect first preserves a backup. API reachability and effect render confirmation are distinct: the effect reports its scene-draw counter to a loopback endpoint using the active session token. Stale sessions are rejected; stopping streaming invalidates the session. Physical LED output remains outside this confirmation.

## Scenes and operational diagnostics

Named scenes store typed, versioned source snapshots independently of global settings. Save, replace, rename, delete, import and export have explicit actions. Import validates size, source count, device and geometry fields before changing the library. Loading uses the same undo and capture lifecycle as restoring editor history, preserving recording and pause intent. Shutdown flushes pending scene writes.

Per-source diagnostics record received, produced, dropped and skipped frames, processing duration, capture failures, encoder mode and source send rates. Counters belong to a capture generation so late callbacks cannot corrupt a replacement session. Rate samples use bounded buckets; the diagnostics UI refreshes once per second. The SignalRGB sender separately measures complete JPEG transactions and errors. These measurements expose operating behavior without claiming a universal latency improvement.

## Test layers

The geometry suite includes an independent inverse-coordinate oracle for crop hit tests. The streaming harness exercises real loopback HTTP and HTTPS services with an ephemeral, untrusted test certificate, real compositor pixels and an injected slow SignalRGB transport. Capture failure removes stale frames, closes readers and allows recovery on the first valid new frame; an intentional pause keeps the last image.

The view-model harness links the production partials and generated commands, replacing only UI dispatch and external services. It covers delayed capture starts, stop/shutdown races, blocked paste and undo, availability polling, settings failures, detached save snapshots, layer order, mirroring and keyboard movement. These checks do not instantiate a native WinUI visual tree.

Website state tests exercise stale initialization/navigation completion, generation invalidation of captured frames, initialization errors, retry recovery and locale-independent zoom. The production browser's lifetime belongs to its capture session, applies the selected user agent before navigation and shares the configured viewport and zoom with the setup preview. Capture errors reach pipeline diagnostics. These checks validate the state and service logic; real native browser rendering and visibility remain separate integration checks.

Web sources accept HTTP, HTTPS and local `file:///` addresses. The file picker escapes local filenames, and scene import/export preserves the URI. Local HTML remains a normal page; local images are fitted without changing aspect ratio. Local videos use a small generated browser document with muted, automatic, looping playback. Its virtual hostname maps the selected folder inside that browser controller; no HTTP server or external file sharing is created. Capture and dialog preview use the same media presentation helper. Video codec support is the installed WebView2 runtime's support.

New sources use WebView2's actual user agent by default. Explicit saved overrides remain intact. The setup preview has an independent browser session, so its temporary scroll/form/login state is not advertised as copied to capture.

## Reproduced encoder failures

On the development machine, native recording rejected a 2 × 2 output with an invalid Media Foundation media type. Hardware recording also failed on small dimensions including 100 × 80 and 64 × 64, sometimes after preview frames had already arrived. Tests now wait through encoder finalization rather than declaring success after the first image.

ScreenRecorderLib also uniformly fits its preview into `VideoFramePreviewSize`: merely using a larger encoder surface can change the preview aspect ratio. The production options and JPEG processing are shared with the native test harness to test the complete size conversion rather than a duplicated approximation.

The compatibility surface size is an implementation choice validated on this machine, not a claim that every H.264 encoder has the same minimum. Hardware rejection falls back once to software for recognized encoder/sink-writer errors. Missing devices and other capture failures remain visible errors.

## Validation commands

Run from the repository root on Windows with the .NET 10 SDK and Windows build tools:

```powershell
dotnet build Better-SignalRGB-Screen-Capture/Better-SignalRGB-Screen-Capture.csproj -c Debug -p:Platform=x64
dotnet build Better-SignalRGB-Screen-Capture/Better-SignalRGB-Screen-Capture.csproj -c Release -p:Platform=x64
dotnet run --project tests/BetterSignalRGB.RegressionTests -c Release
dotnet run --project tests/BetterSignalRGB.StreamingTests -c Release
dotnet run --project tests/BetterSignalRGB.ViewModelTests -c Release
dotnet run --project tests/BetterSignalRGB.NativeSmokeTests -c Release
node tests/StreamingEffectTests.cjs
npm ci --prefix tests --ignore-scripts
Push-Location tests
npx playwright install chromium
npm run test:browser
Pop-Location
```

The independent native web capture harness uses only generated local content and never captures the desktop:

```powershell
node tests/GenerateLocalMediaFixture.cjs
dotnet run --project tests/BetterSignalRGB.WebsiteHostTests -c Release -p:Platform=x64
```

It checks live JavaScript/CSS animation pixels with no visible application window, configured dimensions and user agent, stop/restart, cancellation during initialization, and local media paths containing spaces, accents and `#`.

The deterministic checks cover model validation, history, atomic persistence, region/DPI geometry, rotated resize anchors, bounded frame delivery, cancellation, multipart framing and crop/mirror calculations. The JavaScript harness checks the actual effect script and the web canvas script. The browser suite checks eleven synthetic scene fixtures, actual rendered filters, 34,046 independent geometry samples and the effect's real loopback render-feedback request. CI runs the build, deterministic checks and headless browser comparisons; it does not capture the CI desktop.

For the native capture matrix on an interactive Windows desktop:

```powershell
dotnet run --project tests/BetterSignalRGB.NativeSmokeTests -c Release -- --capture-display
dotnet run --project tests/BetterSignalRGB.NativeSmokeTests -c Release -- --capture-window --capture-webcam
```

This test briefly captures connected displays into memory and reports only dimensions and counts. It saves, displays and transmits no captured pixels. It exercises tiny and odd dimensions, portrait and ultrawide sources, bounded large outputs, region clipping, multiple displays when available, and stop/restart.

The source-specific native run verified a synthetic window while completely covered, restart recovery, and all four camera devices available on the workstation with Auto and an explicit supported format. Region preview PNG dimensions can be rounded by one pixel by ScreenRecorderLib; streamed JPEG dimensions remain exact. Without opt-in flags, the native harness tests encoding, device identity and the real website capture service with a simulated dispatcher/browser, without capturing the desktop. See [native source results](native-source-smoke-results.txt).

Wallpaper Engine is implemented as a selectable monitor source. Its verified wallpaper-only `WorkerW` host is captured in an owned helper process, with bounded frame transport, exact output dimensions and cleanup on cancellation, timeout or missing surfaces. The app never falls back to capturing the desktop or replaying wallpaper assets. Actual web, scene and video renderers each produced six distinct JPEGs while completely covered. The real application's worker entry point, tiny/odd dimensions and stop/restart were also exercised. See [Wallpaper Engine implementation and validation](wallpaper-engine-feasibility.md) for the renderer matrix, restored user configuration and the producer's own pause/stop boundary.

## Remaining verification boundaries

Final local validation on 2026-09-27: x64 Debug and Release builds succeeded (36 existing MVVM AOT advisories, no errors); 2,034 geometry/model/persistence/XAML assertions, 188 view-model assertions and 156 compositor/HTTP/HTTPS assertions passed. Native synthetic capture checks and the independent offscreen WebView2 harness passed, including HQ lossless browser input and local media. Chromium verified the raw output page's actual changing pixels, live HQ switch and reconnect, plus the eleven effect fixtures and independent geometry oracle. The bundled SignalRGB effect matches the source file by SHA-256. The user's open application was not restarted for these final changes; no live WinUI gesture validation was performed after UI automation was interrupted.

- No whole-application before/after CPU, GPU or frame-latency percentage is claimed. Bounded queues, removed duplicate work and reduced UI work are established by code and tests; end-to-end performance still depends on the GPU, drivers, sources and SignalRGB.
- Hardware coverage is the available Windows machine. Other GPU encoders, webcam drivers, protected windows, HDR and display hot-plug require device testing.
- Automated geometry/protocol checks are not a substitute for a manual WinUI interaction and visual comparison in SignalRGB. Website capture, navigation and mixed-DPI display behavior should also be exercised interactively.
- Resize/crop handles currently scale with the logical canvas zoom. Rotated crops and groups are constrained to transforms representable by the existing rotation/scale model; skew is not introduced.
- ScreenRecorderLib still creates an internal H.264 carrier even though this application consumes JPEG previews. Its bytes are discarded; completely removing that encoding work requires a separate native capture backend and its own hardware compatibility validation.
- The project retains its existing Windows App SDK and .NET package versions. MVVM generator AOT advisories from field-based observable properties are separate from the validated ordinary x64 build.
