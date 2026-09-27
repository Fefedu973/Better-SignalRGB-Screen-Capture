# Wallpaper Engine: capture of the existing rendered surface

Research and implementation notes, 2026-09-27. The initial production backend now captures the existing wallpaper-only WorkerW with an isolated PrintWindow worker. No persistent Wallpaper Engine configuration changes were made. Native validation temporarily selected scene/video wallpapers, then restored and verified the original selections, properties and settings.

The requirement is to capture the actual running wallpaper, including its current animation, interactions and configured properties, while other windows cover it. A duplicate `playInWindow` instance, reopening its video file, replaying its scene or rebuilding its web page do not meet this requirement.

## Verified interfaces

Wallpaper Engine's public command line API exposes `getWallpaper` per monitor or location, returning a file path. It does not expose pixels, a GPU texture handle, animation position or a rendered-frame subscription. Polling it can identify wallpaper changes, but cannot capture the rendering or establish synchronized state. [Official CLI documentation](https://help.wallpaperengine.io/en/functionality/cli.html).

The official designer APIs target individual scene/web wallpapers. The RGB interface sends a wallpaper-authored canvas to the existing LED plugin; it is not a general readback API for other wallpapers. [Designer reference](https://docs.wallpaperengine.io/en/scene/scenescript/reference.html), [RGB API](https://docs.wallpaperengine.io/en/web/api/rgb.html).

Developer Tim stated in July 2021 that the plugin interface was not public. Current official documentation and the installed 2.8.485 distribution still yielded no public frame-export SDK during this search; the historical statement alone is not proof of all current private capabilities. [Developer statement, comment 2](https://steamcommunity.com/app/431960/discussions/2/5190945662888293197/?l=turkish).

Read-only inspection of the installed PE exports found:

| Module | Relevant exports | Meaning established |
| --- | --- | --- |
| `plugins/led/ledextensions64.dll` | `CreateWPExtPlugin`, `GetWPExtPluginVersion` | Private plugin entry points exist; no headers or callable ABI were found. |
| `bin/cloneextensions64.dll` | `CreateClone`, `DestroyClone`, `SetCloneRect`, `CreateWindowCompositionForSwapChain` | Internal composition facilities exist; names do not establish a supported frame-sharing contract. |
| `bin/mediaextensions64.dll` | `CreateMediaExtensions`, `WallpaperEngineMediaExtensionVersion` | Internal media extension entry points, not a documented capture interface. |

## Local host capture evidence

The native probe found `Progman -> WorkerW -> Wallpaper Engine renderer`. `SHELLDLL_DefView` is a sibling of this WorkerW; its icon list is currently invisible. Thus the wallpaper-only WorkerW can be selected by ancestry, without depending on Chromium child class names.

The companion native probe reported:

- WorkerW is currently a child window: direct WGC returns invalid argument.
- WorkerW `PrintWindow` produced 12 distinct hashes out of 12 frames, both uncovered and covered. Median capture durations were approximately 66.3 ms and 70.5 ms at 4880x2560.
- Progman WGC produced changing frames under verified coverage. However, its tree also contains the desktop icon branch. With icons currently hidden, this does **not** establish that Progman capture excludes visible icons.

Those first timing measurements concerned the running web wallpaper. Subsequent production-service tests also exercised an actual scene wallpaper (`beach`) and an actual video wallpaper (`scene-rainbow.mp4`) on the desktop, then restored the original wallpaper. Each type produced six distinct 319x199 JPEGs under full verified occlusion through the same WorkerW backend. The scene case retained 500 sampled colors while covered. Both display selections and the complete settings were verified unchanged after restoration. This establishes the three renderer families on the current local Windows/Wallpaper Engine installation; it does not establish every OS, graphics driver, wallpaper or display mode.

## Source architecture

Implemented: exact WorkerW discovery, icon-tree rejection, executable-path verification, physical monitor crop, a hidden worker launched from the same executable, bounded binary BGRA frames, one pending frame in the JPEG mailbox, five-second worker timeout, owned-process termination, cancellation, periodic discovery and standard capture-failure cleanup. Requested FPS is honored; actual rate depends on capture cost and is visible in diagnostics. The worker retains reusable native DIBs, with a 64-megapixel native surface ceiling, and output is bounded by CaptureGeometry.

Current native integration tests passed for the installed web wallpaper: six decoded 319x199 JPEGs with six distinct hashes both normally and under full verified desktop occlusion; stop/restart at 3x5; no remaining helper after stop; missing-display failure and frame cleanup. The occluder stays immediately above the desktop and below existing applications, with geometry and z-order checked on every captured frame. Its creation does not activate it; subsequent user interaction is allowed. The actual Release application executable also produced framed BGRA pixels through its custom worker entry, exited after cancellation, and rejected invalid arguments without launching the GUI. Deterministic tests verify timeout/cancellation of a deliberately blocked helper, bounded stderr, malformed/truncated frames, icon-host exclusion and negative monitor coordinates. No captured images were persisted or sent to external applications by these tests.

The following architecture includes two follow-up optimizations, **not yet implemented**: WGC selection for top-level wallpaper-only hosts, and shared capture across multiple source items. Currently each source owns its isolated helper; duplicates of the same physical host therefore repeat its PrintWindow cost.

1. Add a `WallpaperEngine` source with a stable display identifier (or an explicit entire-wallpaper-surface option). Do not persist a discovered HWND as the source identity.
2. Discover running Wallpaper Engine renderer processes from their verified executable paths. Enumerate their actual visible descendant windows and their parent chain. Identify the owning wallpaper WorkerW by the presence of a renderer descendant, not by taking the first WorkerW on the desktop.
3. Reject any candidate subtree containing `SHELLDLL_DefView` or `SysListView32`. Do not silently fall back to Progman when that would include desktop icons. Do not hide icons, reparent foreign windows or change Wallpaper Engine playback settings.
4. Prefer WGC when that wallpaper-only host is a capturable top-level window. Otherwise use the proven WorkerW PrintWindow path as a distinct backend, with renderer compatibility still requiring the tests below.
5. Capture each shared host once, then crop its actual pixels for selected monitors. Calculate host-to-monitor coordinates using physical desktop rectangles and the actual captured dimensions, including negative coordinates and mixed DPI. Multiple source items should subscribe to the same producer instead of printing the same 50 MB desktop surface repeatedly.
6. Downsample/crop into the existing bounded BGRA/JPEG pipeline, with one latest pending frame and no queue. This backend does not need a discarded H.264 carrier. Diagnostics should identify the composition capture backend separately from hardware/software H.264.
7. Recheck process/host identity and geometry on display changes, renderer replacement, Explorer restart and wallpaper changes. Coalesce discovery work. Validate handle ownership to prevent reuse. A playlist transition may keep the same host while replacing descendants; preserve the producer when its validated host remains usable.
8. When the host disappears, stop publication, release native resources and raise the normal capture failure after a short bounded transition grace period. Paused or static wallpaper pixels remain valid; repeated hashes do not prove a failure or even a pause. Expose capture rate and last content change separately.

PrintWindow is synchronous and can block. A timeout around `Task.Run` does not cancel it. The implementation isolates it in an app-owned helper process and terminates only that helper on timeout or stop, never Wallpaper Engine or Explorer. DC/DIB resources are reused. There is no hidden FPS cap; the user-selected rate is limited naturally by capture duration, and the parent drains frames into the bounded latest-frame mailbox. [Microsoft PrintWindow contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow).

DWM thumbnails do not directly solve child-window readback: their source and destination must be top-level windows, and the result is rendered into the destination window rather than returned as pixels. [Microsoft DwmRegisterThumbnail](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmregisterthumbnail).

## If host capture cannot cover a renderer

A native graphics bridge remains a technically plausible research path: intercept the existing renderer's presentation and copy its actual backbuffer into a bounded shared texture. OBS demonstrates this architecture with DXGI Present/Present1 and D3D11 shared textures. This is implementation evidence, not proof that its existing hook captures every Wallpaper Engine renderer. [OBS DXGI hook](https://github.com/obsproject/obs-studio/blob/master/plugins/win-capture/graphics-hook/dxgi-capture.cpp), [OBS D3D11 copy](https://github.com/obsproject/obs-studio/blob/master/plugins/win-capture/graphics-hook/d3d11-capture.cpp).

There is no general operation that opens an arbitrary foreign GPU resource from a PID or HWND. Shared-resource APIs require an appropriate shared resource and its handle. Composition swap chains also lack a valid GetHwnd, so web GPU subprocesses, media/composition surfaces, multiple swap chains, output mapping, HDR and device loss need explicit treatment. A single Present hook is not a justified universal implementation. [Shared resource contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiresource-getsharedhandle), [Composition swap-chain contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition).

## Playback and acceptance criteria

Wallpaper Engine can pause or completely unload wallpapers according to its performance policies. A capture backend cannot produce new authentic frames after the producer stops rendering. While the verified host remains available, static or paused rendered pixels continue to be captured; repeated images do not establish why they are unchanged. If the host is unloaded or disappears beyond the bounded transition grace period, capture fails explicitly and clears its cached frame through the standard failure path. The application does not force global play or alter those policies. The current performance settings were only read: fullscreen pauses, maximized windows keep running, display sleep stops, and the renderer FPS setting is 60. Renderer-family validation temporarily selected actual scene/video wallpapers and restored the original selections and properties. [Official playback behavior](https://help.wallpaperengine.io/en/performance/game.html).

Scene, video and web were verified on their actual desktop instances under full occlusion, including stop/restart and missing-display failure cleanup. Before shipping an unqualified universal claim, additional environments still need coverage: icons shown, separate monitor arrangements, mixed DPI, playlist changes, renderer/Explorer restart, HDR, and sustained CPU/memory/latency measurements. Compare known synthetic content or in-memory image statistics without persisting user images. Current tests establish the installed three renderer families on the existing spanning desktop configuration.

## Reproducing the native checks

`dotnet run --project tests/BetterSignalRGB.NativeSmokeTests/BetterSignalRGB.NativeSmokeTests.csproj --no-restore -p:Platform=x64 -- --capture-wallpaper` runs the actual capture service, a brief desktop-only opaque cover, exact odd JPEG sizing, worker stop/restart and missing-display failure cleanup. It captures the wallpaper already running; it never changes its selection or playback policy. Only dimensions, counts and in-memory image statistics are printed.

The same harness accepts `--capture-wallpaper-exe <absolute application exe path>` to verify the packaged Release application's worker entry, frame protocol, cancellation and invalid-argument handling. The normal invocation without native capture arguments runs synthetic protocol, geometry, fake-browser lifecycle, blocked-worker timeout and bounded-stderr tests.
