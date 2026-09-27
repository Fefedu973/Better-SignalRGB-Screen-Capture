# Native frame surface verification

This console project links the production `FrameSurfacePublisher.cs`. Its native helper
includes the **actual external** `OpenRGB-Room/FrameSurface/FrameSurface.h`; it neither
copies nor substitutes the C++ `Reader`. All images, channels and crashed processes are
synthetic and owned by this harness. It never opens capture devices, the app UI or LEDs.

Run from the repository root on Windows, with .NET 10 and a C++17 compiler:

```powershell
& tests/BetterSignalRGB.NativeOutputTests/Run-Tests.ps1 `
    -OpenRgbRoot ../OpenRGB-Room -Cxx g++.exe
```

`cl.exe` is also supported when called from an initialized MSVC developer shell.
`-SkipBenchmark` omits only the paced benchmark. The script writes the resolved header
path, SHA-256, external Git revision and compiler path to `obj/native/provenance.json`.
The hash is compiled into the native helper and checked by the C# harness; the script
also checks the source header has not changed during the run. No external file is edited.

## Publisher contract

- `FrameSurfacePublisher(channel, requestedCapacity)` creates or safely reclaims one
  latest-frame surface. Channel names are 1–64 ASCII letters, digits, `_` or `-`.
  Capacity is 1–64 MiB. The app supplies `800 * 600 * 4` for its current maximum output.
- Constructor failures throw; they never steal a live producer. Creation may wait up to
  100 ms for the ownership transaction. Construct and dispose on the output worker.
- `TryPublish(ReadOnlySpan<byte>, width, height, stride)` accepts opaque BGRA8/sRGB,
  copies pixels synchronously and zeroes row padding. It never retains the caller's
  buffer. The caller must keep that buffer stable until the call returns.
- Both the local lock and the shared mutex are attempted without waiting. Contention
  returns `false`, leaves the preceding valid image intact, and does not enqueue work.
  There is one bounded shared image, not a frame history. This is **not zero-copy**.
- `Generation` is stable for the publisher lifetime. `Sequence` starts at zero and
  advances only after a successful complete publication. The orchestration layer
  creates new publisher generations when effective scene metadata changes.
- `Heartbeat()` renews only the timestamp of an existing frame. It does not copy pixels
  or advance sequence. Call it from the healthy snapshot/publication worker: a separate
  timer would conceal a worker hang. Reader TTL detects a living but stalled producer.
- The unsignaled generation-specific lifetime event retires before disposal waits for
  an in-flight local copy. A reader holding the shared mutex cannot prolong this marker.
  Closing or crashing the publisher makes the actual C++ reader report `Unavailable`.
- A mapping held open by a reader cannot grow. Reopening at the same or smaller capacity
  reuses the actual existing capacity with a new generation. Close all readers first to
  increase capacity; preallocating the app's bounded maximum avoids an HQ resize trap.
- All three kernel objects use a protected DACL granting only the current **process**
  user access. Existing mutex/mapping permissions are checked and rejected if broader;
  the publisher does not silently rewrite another object's permissions.
- `LastError` describes the most recent attempt; concurrent calls can replace it. Treat
  the returned boolean as the result of a particular call. `Dispose()` is idempotent.

The external reader checks the current shared-header timestamp before deciding
`Unchanged`, but leaves its returned `Frame.timestamp_ms` cached in that case. Consumers
must regard successful `Unchanged` as live, rather than re-expiring that cached timestamp.
This behavior is tested against the real header, not assumed.

## Evidence and bounds

Verified on 2026-09-27, Windows x64, Intel Core i7-12700KF, .NET runtime 10.0.0-preview.5.25277.114,
SDK 10.0.100-preview.5.25277.114, MinGW-w64 GCC 13.2.0. External source:

- Git revision: `c870793e15e48a3e62a868a3a0ff0f6992b59f84`
- Header SHA-256: `EBFD6E8BD449D4735DEF03E2B03C14727797A9CBB318584AB60B5D3CF8C8A17D`
- Result: **378 assertions passed**, including 180 paced frame publications in the
  benchmark/other scenarios; no managed allocations in the successful warmed-up
  publication path. The exact assertion total includes per-publication assertions.

Coverage includes exact ABI fields and reserved bytes; odd dimensions; padded rows;
opaque alpha and bounds; owned copy; latest-only bursts; two independent native readers;
no torn images under contention; immediate drop when a reader holds the mutex; abandoned
mutex recovery; static heartbeat and stale-worker TTL; corrupt header rejection by the
actual Reader; lost ownership; concurrent publication/disposal; disposal while a reader
holds the mutex; process crash; retained mapping capacity; restart with a new generation.
ACL assertions inspect effective descriptors for the current identity; they do not
claim an interactive second-user logon was exercised.

The same 196 non-benchmark assertions also passed with a **32-bit C# producer**
(.NET 10 preview 5, x86) communicating with the same **64-bit C++ reader**. This
checks the interop layout and handle marshalling across process architectures.
The same 196 assertions also passed with .NET **10.0.10 stable, x64**, using
`dotnet exec --fx-version 10.0.10` on the built test DLL. The native reader/compiler
itself was x64; ARM64 execution was not exercised.

Publication benchmark, 90 frames each, target 30 fps, same public API, 1,000 warmup
publications, no concurrent reader:

| Image | Actual fps | Publish p50 | Publish p95 | Max | Whole-process CPU over ~3 s | Managed bytes |
|---|---:|---:|---:|---:|---:|---:|
| 320×200 | 29.93 | 54.5 µs | 126.2 µs | 217.2 µs | 156.25 ms | 0 |
| 800×600 | 29.94 | 410.5 µs | 640.5 µs | 953.5 µs | 31.25 ms | 0 |

These timings include alpha validation, mutex acquisition and the synchronous copy.
They exclude scene composition, JPEG encoding, capture and OpenRGB rendering/LED output.
Whole-process CPU is coarse and includes runtime/JIT, timer pacing and harness overhead;
its nonmonotonic result is not a per-frame CPU comparison. Scheduler load and power
state affect the timings; this is a local observation, not a cross-hardware guarantee.
The integration harness must establish metadata/image/coverage agreement separately.

## Native helper protocol

`obj/native/FrameSurfaceReader.exe <channel>` writes `READY <header SHA-256>`, then accepts:

- `read <TTL-ms> <lock-timeout-ms>`: returns `Status Width Height Stride Sequence
  Generation FrameTimestamp Fnv1a64 PaddingZero UniformSyntheticPattern`.
  The hash is FNV-1a-64 over the copied payload including padding. The final pattern flag
  is only meaningful for this harness's uniform synthetic frames.
- `close`: closes the real Reader's handles, responds `CLOSED`.
- `quit`: orderly exit.
- `hold <ms>`, `abandon`, `stress <ms>` are bounded fault/concurrency test commands.

The included test helper is GPL-2.0-or-later to match its external header dependency.
It is a test artifact, not a companion process required by the production publisher.
