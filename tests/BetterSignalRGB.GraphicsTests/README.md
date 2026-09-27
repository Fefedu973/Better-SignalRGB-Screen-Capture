# GPU and Windows Graphics Capture checks

This Windows x64 console harness links the production `GraphicsFrameCapture`,
source selection, display metadata and HDR tone mapper. It uses the application's
Win2D 1.3.2 and Windows App SDK versions, with self-contained runtimes. It creates
no application window by default and never changes display settings or a saved profile.

```powershell
dotnet run --project tests/BetterSignalRGB.GraphicsTests -c Release -p:Platform=x64
```

The default run sends synthetic values above one through real FP16 GPU textures,
downsampling and readback, then checks the production HDR/SDR conversion. It does
not capture a display. An interactive Windows graphics session is required.

Explicit local capture checks (six seconds each):

```powershell
dotnet run --project tests/BetterSignalRGB.GraphicsTests -c Release -p:Platform=x64 -- --monitor --region
```

`--monitor` selects an active HDR monitor. `--region` requires a horizontally
adjacent HDR/SDR pair and captures a narrow region spanning their boundary. Both
use the production capture backend; only frame counts, timing, dimensions and
color-conversion status are printed. No pixels, frame hashes or images are saved.
Windows may show its capture indicator if it requires one. The tests do not
activate, move, hide, stop or change the user's application windows.

Additional opt-in checks:

```powershell
dotnet run --project tests/BetterSignalRGB.GraphicsTests -c Release -p:Platform=x64 -- --window --preview
```

`--window` creates only its own nonactivating GDI fixture, behind other windows
on the HDR display. Its white, gray and black quadrants verify the real window
capture's SDR-white calibration. Colored markers prove fresh full-size frames
after growing and shrinking the window. Three rapid first-frame/cancel cycles,
pre-cancellation and invalid target/dimension cases check cleanup. The fixture
closes at the end; the user's windows are never selected or manipulated.
On the local HDR display at 480-nit SDR white, this produced white 240, gray 128
and black 0 at each of 320×200, 640×360 and 200×300 input sizes. The output stays
320×200. These are digital capture values from the known GDI fixture, not a
measurement of the physical panel or LEDs.
The same native fixture calibration, resize and cancellation checks also passed
with the x86 runtime on this machine:

```powershell
dotnet run --project tests/BetterSignalRGB.GraphicsTests -c Release -p:Platform=x86 -p:PlatformTarget=x86 -r win-x86 --no-launch-profile -- --window
```

`--preview` runs the production one-shot region helper on an odd-size HDR region
and the mixed-monitor negative-Y boundary. It decodes the JPEG only in memory,
checks bounded exact dimensions and confirms the WGC worker has stopped before
returning. It requires the same HDR/SDR arrangement as the local mixed-region
test, including the SDR monitor extending above the HDR monitor. No image files
are saved.

These checks establish buffer precision and capture lifecycle on the local GPU.
They do not compare the user's desktop colors, emulate a physical HDR display's
tone mapping, or validate OpenRGB LEDs. A static desktop may produce few frames.
