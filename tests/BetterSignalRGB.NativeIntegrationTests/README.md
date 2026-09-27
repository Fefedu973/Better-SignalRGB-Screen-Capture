# Native integration tests

This console harness links the **production** compositor, raw buffer leases, ORGBFRM1 publisher, native output worker, authenticated Kestrel API, descriptor and integration lifecycle. Only the capture producer, application scene/UI boundary and saved-settings storage are substituted. All image inputs are synthetic JPEGs. It does not start WinUI, capture any screen/window/camera, contact an OpenRGB instance, or modify a real user profile.

The test creates a random profile below `tests/obj/native-integration/`, with its own current-user-only connection descriptor and surface channels. It stops the listener and publisher, removes credentials and cleans that owned profile in `finally`. No tokens are logged. A failed or interrupted process cannot retain ownership of its channels after exit; leftover test directories remain under the ignored test output directory.

## Run

Windows and the .NET 10 SDK are required. From the repository root:

```powershell
dotnet run --project tests/BetterSignalRGB.NativeIntegrationTests -c Release
```

For the joined interoperability proof, first build the actual Room `FrameSurface.h` Reader using the sibling harness (MSVC x64 Build Tools, with `-Cxx` set as appropriate):

```powershell
& tests/BetterSignalRGB.NativeOutputTests/Run-Tests.ps1 -OpenRgbRoot '<OpenRGB-Room checkout>' -Cxx cl.exe -SkipBenchmark
dotnet run --project tests/BetterSignalRGB.NativeIntegrationTests -c Release -- --reader tests/BetterSignalRGB.NativeOutputTests/obj/native/FrameSurfaceReader.exe
```

Equivalent wrapper after the Reader has been built:

```powershell
& tests/BetterSignalRGB.NativeIntegrationTests/Run-Tests.ps1 -ReaderPath tests/BetterSignalRGB.NativeOutputTests/obj/native/FrameSurfaceReader.exe
```

Without `--reader`, C# reads the real shared mappings under their named mutex for integration assertions; the output explicitly says the C++ Reader was not requested. With it, the actual C++ Reader consumes normal raw, normal coverage and HQ raw frames produced by the complete Better pipeline. Dimensions, generation and FNV-1a hash must match the independently read bytes. The C++ source/header and provenance are owned by `BetterSignalRGB.NativeOutputTests`; this harness does not copy or replace the Reader.

## Coverage

- Default-disabled activation, isolated ephemeral IPv4 loopback listener, user-only credential ACL and disable/re-enable credential rotation.
- Bearer authentication for every read and mutation route; browser Origin/Sec-Fetch-Site and foreign Host rejection; no inherited streaming CORS; unsupported/unknown/malformed/oversized JSON rejection.
- Discovery, scenes, active scene, immutable generation lookup and current status; the public status does not disclose an owner's lease capability.
- Synthetic JPEG sources through the actual compositor into opaque BGRA image and grayscale coverage surfaces; black-but-covered versus absent pixels; no composite JPEG encoding when native output is the only consumer.
- Immutable first-sequence state binding, latest image sequences, separate matching coverage generation/sequence, static heartbeat without new image sequence, HQ resize and source failure.
- Recovery after a transient application-health timeout with a static image. Also rejection and recovery of the **first already-composed frame** after its health check times out, without another capture frame arriving.
- Real Raw/Coverage mutex contention: brief waits are tolerated; failures lasting two seconds report the failed operation, and release restores health without changing static generations. Successful heartbeats cannot hide repeated publication failures.
- Effective 200 acknowledgement only after corresponding pixels/metadata publish; 202 while the application snapshot is blocked; scene identity and geometry match the resulting image.
- Lease conflicts/renewal, metadata-only appearance, manual preference precedence, release/expiry restoration, and API availability while capture is paused or stopped.
- Awaited shutdown closes surfaces, listener and descriptor.

This is not a live application GUI test or a benchmark. The deterministic scene adapter does not replace the separate tests of the actual ViewModel's scene loading, undo, capture-intent preservation and dispatcher behavior. Passing a joined Reader test proves transport compatibility for these synthetic scenes, not a connected OpenRGB Effects shader or physical LED output.
