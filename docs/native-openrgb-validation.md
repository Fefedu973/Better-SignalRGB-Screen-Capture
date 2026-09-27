# Native OpenRGB output: validation and measured costs

Verified locally on 2026-09-27 against Better base `ff335ac86fbf9e8f72f17c87113e2e5753a64606`
(1.3.0), with the changes in this working tree. This report covers the **Better producer**.
It does not claim a connected OpenRGB Effects renderer or working physical lighting.

## Delivered

- Optional in-process ORGBFRM1 raw canvas and synchronized opacity coverage, independent
  of web viewers. The native-only path performs no composite JPEG encoding.
- Authenticated, versioned local discovery/status and generation-bound rendering metadata.
- Saved-scene selection through the application's existing lifecycle, one exclusive
  expiring control lease, and temporary native appearance metadata with manual-edit precedence.
- A settings switch/status, [API and receiver handoff](native-openrgb-integration-v1.md),
  [rendering contract](native-rendering-v1.md), and thirteen synthetic reference scenes.

The transport helper includes the actual external `FrameSurface/FrameSurface.h` at
OpenRGB revision `c870793e15e48a3e62a868a3a0ff0f6992b59f84`, SHA-256
`EBFD6E8BD449D4735DEF03E2B03C14727797A9CBB318584AB60B5D3CF8C8A17D`.
That commit's availability on the public fork was also checked. No changes to the
OpenRGB or Effects repositories are required to run the producer tests.

## Local verification

| Check | Result and scope |
| --- | --- |
| Release x64 WinUI build | Passed, zero errors; 36 existing MVVMTK0045 observable-field/AOT warnings remain |
| Regression suite | 2,192 assertions passed |
| Real ViewModel scene/control harness | 237 assertions passed, including running/paused/stopping transitions, expiry, manual changes and deferred restoration |
| Native lifecycle fault injection | 63 assertions passed: late initialization after shutdown, independent cleanup despite stop failures, failed activation rollback and disposal errors |
| Compositor and HTTP/HTTPS | 252 assertions passed, including native buffer ownership, coverage, stale layout rejection, HQ and no native-only composite JPEG encode |
| Native transport with actual C++ Reader | 378 assertions with the paced benchmark; 196 non-benchmark assertions also passed for x86 producer → x64 reader and x64 .NET 10.0.10 stable |
| Native geometry | 8,018 assertions passed, including an independent inverse-transform membership oracle |
| Native control/compositor/transport integration | 232 assertions passed with the actual C++ Reader; synthetic raw, coverage and HQ images agree in dimensions, generation and pixel hash; prolonged contention, precise failure status and recovery also verified |
| Existing effect protocol and Chromium suite | Passed; production web/SignalRGB pixels, crop/rotation/mirror oracle, rendering controls, preview and reconnect checks |
| Capture service regressions | Passed without real capture: encoding and device identity, website lifecycle, HQ caches, Wallpaper Engine framing and isolated-worker timeout/cancellation |
| Native renderer references | Thirteen scenes / 52 reference PNGs passed; zero interior silhouette disagreement and measured antialias mean error at most 0.547/255 |
| Actual WinUI process | Passed in a separate temporary profile: authenticated discovery, temporary appearance, frame-effective acknowledgement, actual C++ reading, static heartbeat and release without persisted appearance |

The WinUI smoke test uses an empty scene with capture explicitly disabled. It proves
production dependency wiring and process startup; the image-pixel and scene-transition
proofs come from the separate synthetic integration/ViewModel suites. It does not
exercise the user's window, camera, screen or LEDs. The installed application was not
replaced during these development tests. The smoke script stops only the process it created and leaves its isolated logs
for inspection. Unit/integration output and local evidence logs live in ignored `tests/obj`.
The CI workflow includes these automated suites and pins the external Reader contract;
this development report precedes the release workflow. See the release's GitHub Actions runs for packaging and CI results.

## Measurements

Host: Windows x64, Intel Core i7-12700KF; .NET runtime and SDK
10.0.0-preview.5.25277.114 / 10.0.100-preview.5.25277.114. Timings below are local synthetic
observations, not hardware-independent guarantees or a before/after application benchmark.

### Composition and encoding

Same two-source scene, rotations/crops/opacity, alternating synthetic 100×60 source JPEGs,
target 15 fps at both output sizes. Ten warmup frames followed by 90 measured frames per
case. Native output here includes composition and owned raw/coverage copies, but excludes
the publisher, reader, capture devices and GPU upload. The JPEG-enabled cases have both
the raw subscriber and the existing composite JPEG subscriber.
The composition column times source decode/drawing; the event-to-raw column also includes
coverage rasterization, owned plane copies and the compositor's scheduling delay.

| Output | Composite JPEG | Actual fps | Compose p50 / p95 (ms) | Encode p50 / p95 (ms) | Source event → raw p50 / p95 (ms) | Process CPU (ms; % of one core) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 320×200 | Off | 12.89 | 1.14 / 2.02 | 0 / 0 | 48.14 / 49.36 | 531.25; 7.61% |
| 320×200 | On | 12.91 | 1.15 / 2.33 | 0.23 / 0.50 | 48.31 / 50.20 | 187.50; 2.69% |
| 800×600 | Off | 13.93 | 18.28 / 21.15 | 0 / 0 | 64.66 / 68.47 | 1,796.88; 27.81% |
| 800×600 | On | 14.13 | 18.80 / 24.66 | 1.11 / 1.72 | 65.90 / 72.90 | 2,015.63; 31.66% |

The native-only cases recorded **zero composite JPEG encodes**, versus 100 including
warmup in the JPEG-enabled cases. Source JPEG decoding remains part of composition.
The existing compositor delay of about 33 ms remains; Windows timer scheduling increased
the observed event-to-raw delay. Thus transport microseconds must not be reported as the
application's frame latency. Achieved cadence did not equal the target in this harness.
Process CPU includes runtime/scheduling overhead: the inverted 320×200 CPU results are
no evidence of an optimization or regression. Repeat under controlled conditions before
making a comparative CPU claim.

### Shared-memory publication

Separate warmed-up publisher measurement, target 30 fps, 90 samples per size, 1,000 warmup
publications, no concurrent reader. This measures opacity validation, mutex acquisition
and synchronous copy, without the compositor or coverage orchestration.

| Output | Actual fps | Publish p50 / p95 | Max | Managed allocation |
| --- | ---: | ---: | ---: | ---: |
| 320×200 | 29.93 | 54.5 / 126.2 µs | 217.2 µs | 0 bytes |
| 800×600 | 29.94 | 410.5 / 640.5 µs | 953.5 µs | 0 bytes |

Whole-process CPU was 156.25 ms and 31.25 ms respectively over approximately three
seconds, including timers/JIT/harness overhead. This coarse, nonmonotonic measurement
is not a per-frame CPU comparison. Full provenance and transport scenarios are in the
[publisher test documentation](../tests/BetterSignalRGB.NativeOutputTests/README.md).
Reader-copy time, GPU upload, native shaders and capture-to-LED latency remain unmeasured.
The transport copies CPU pixels; it is not zero-copy.

## Reproduce

From the repository root on Windows with .NET 10, the existing test Node dependencies
and Playwright Chromium installed:

```powershell
dotnet build Better-SignalRGB-Screen-Capture/Better-SignalRGB-Screen-Capture.csproj -c Release -p:Platform=x64
dotnet run --project tests/BetterSignalRGB.RegressionTests -c Release
dotnet run --project tests/BetterSignalRGB.ViewModelTests -c Release
dotnet run --project tests/BetterSignalRGB.StreamingTests -c Release
dotnet run --project tests/BetterSignalRGB.NativeRenderingTests -c Release
dotnet run --project tests/BetterSignalRGB.NativeLifecycleTests -c Release
dotnet run --project tests/BetterSignalRGB.NativeSmokeTests -c Release

# Use cl.exe from an initialized MSVC shell, or a compatible g++.exe.
& tests/BetterSignalRGB.NativeOutputTests/Run-Tests.ps1 -OpenRgbRoot ../OpenRGB-Room -Cxx g++.exe
dotnet run --project tests/BetterSignalRGB.NativeIntegrationTests -c Release -- --reader tests/BetterSignalRGB.NativeOutputTests/obj/native/FrameSurfaceReader.exe

node tests/StreamingEffectTests.cjs
npm --prefix tests run test:browser
node tests/NativeRenderingReferenceTests.cjs

dotnet run --project tests/BetterSignalRGB.StreamingTests -c Release -- --benchmark-native-composite tests/obj/native-compositor-benchmark.json

# Isolated real application, no capture; use the current build's RID directory.
& tests/Invoke-NativeAppSmoke.ps1 -Executable Better-SignalRGB-Screen-Capture/bin/x64/Release/net10.0-windows10.0.22000.0/win-x64/Better-SignalRGB-Screen-Capture.exe -Reader tests/BetterSignalRGB.NativeOutputTests/obj/native/FrameSurfaceReader.exe
```

## Remaining receiver work

The OpenRGB agent must implement the source selector/provider, discovery and reconnection,
generation-bound raw/coverage acquisition, shared lease management, native texture upload
and GLSL appearance. Validate shaders against the supplied reference RGBA/coverage images
before comparing the complete GDI-based pipeline. Eight-bit flattening, interpolation
and antialiased edges impose the fidelity limits documented in rendering v1.

Then run a connected Better → Effects → physical-device session, measuring sustained
FPS, capture-to-light latency, reconnect and effect-switch behavior. This change does
not replace that end-to-end acceptance test, an ARM64 execution test or a live second-user
ACL test. Release packaging and installation are tracked separately from these development measurements.
