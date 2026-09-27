# HDR-to-SDR conversion tests

Run `dotnet run --project tests/BetterSignalRGB.HdrTests -c Release`.
This dependency-free console harness links the production `HdrToneMapper` and capture
geometry bounds. It generates FP16 pixels in memory and never captures a display,
opens a window, changes HDR settings or reads user images.

`CaptureColorPolicy.Resolve(hdrEnabled, sdrWhiteNits)` selects conversion for each
captured display. Confirmed SDR bypasses HDR processing. An unknown HDR mode also
bypasses it, but remains diagnostically unknown; a successful white-level query alone
must not be mistaken for HDR being enabled. Confirmed HDR with valid white metadata
uses that calibration. Confirmed HDR with missing/invalid white metadata retains
highlight compression at neutral exposure (80-nit reference), but reports unknown
calibration rather than successful calibrated tone mapping. The policy stores no
previous screen state, so it cannot reuse another monitor's reference white.

## Input and output contract

`ConvertRgba16Float(fp16, width, height, stride, bgra, hdr, sdrWhiteNits)` accepts
little-endian **linear scRGB RGBA16_FLOAT**, with explicit source row pitch. Output
is tightly packed **opaque BGRA8/sRGB**. Input and output must not overlap. Input
alpha is ignored: the capture backend must supply its already composited opaque surface.
Dimensions use the existing capture limits (maximum 1920 on either axis, 1920×1080
pixels). The helper owns only fixed lookup tables, retains neither buffer and makes
no per-frame allocations after initialization.

`MapLinearRgb` exposes the same transform before sRGB encoding for independent
readback/GPU fixtures. It returns **linear** output values, not gamma-encoded bytes.

- HDR input is normalized by `80 / sdrWhiteNits`. Windows reports the SDR white
  level as `80 * DISPLAYCONFIG_SDR_WHITE_LEVEL / 1000` nits. For a 480-nit setting,
  reference SDR white is therefore scRGB `(6,6,6)`, not `(1,1,1)`.
- Rec.709 luminance is `L = .2126 R + .7152 G + .0722 B`. The curve is identity
  through `k = .75`; above the knee it uses
  `T(L) = k + (1-k)*(L-k)/(L-k+1-k)`. The value and first derivative are continuous.
  Multiplying all channels by `T(L)/L` preserves their relative proportions.
- Out-of-gamut values move toward neutral grey at `T(L)` with one chroma scale
  until every component is in `[0,1]`. This preserves mapped luminance and channel
  ordering. Very bright saturated colors necessarily lose some saturation to fit
  SDR at that luminance; the mapper does not clip each channel independently.
- A 16,385-entry sRGB lookup table applies the output transfer exactly once, with
  at most one output-code error in the tested 256-level FP16 SDR roundtrip.
- HDR normalized white `1` maps to `.875` linear, **240** in 8-bit sRGB. This is
  the intentional headroom for highlights. It is a deterministic application curve,
  not an assertion of equivalence to BT.2390, ACES, a monitor's tone mapper or the
  Windows Direct2D HDR tone-map effect.
- With `hdr=false`, exposure normalization and HDR shoulder/gamut mapping are skipped.
  Linear FP16 values are clamped and encoded to sRGB. Existing gamma-encoded SDR
  BGRA/JPEG, WebView and GDI captures must bypass this helper entirely.
- NaNs become zero; infinities saturate to the finite Half range before mapping.
  Negative scRGB primaries are retained until gamut conversion. Invalid/unavailable
  SDR-white metadata (nonfinite, below 1 or above 10,000 nits) falls back to 80 nits.

## Verified evidence

2026-09-27: **2,759 assertions passed** on an Intel Core i7-12700KF, Windows x64,
.NET 10 preview 5. Coverage includes 80/160/480-nit normalization, the documented
white point, continuous knee, monotonic highlights, luminance-preserving gamut
compression, SDR gamma roundtrip, all 65,536 Half bit patterns, nonfinite/negative
inputs, odd/padded rows, channel ordering, input immutability, exact destination
bounds, overlapping buffers and invalid dimensions/lengths/strides.
The additional display-policy checks exercise actual mapper output for unknown/SDR
metadata, known HDR without white calibration and transitions between displays.

60 warmed-up CPU conversion samples, synthetic colored HDR pixels at a 480-nit
white setting (zero managed allocations):

| Dimensions | p50 | p95 | Maximum |
|---|---:|---:|---:|
| 320×200 | 1.368 ms | 1.921 ms | 2.428 ms |
| 800×600 | 9.678 ms | 11.713 ms | 13.258 ms |

These local timings exclude GPU capture, downsampling/readback, JPEG encoding and
all lighting renderers. Hardware capture and display-mode transitions belong to the
separate native backend tests. These fixtures do not establish end-to-end HDR accuracy.

## Why the conversion precedes 8-bit capture

The installed ScreenRecorderLib 6.5.1 package uses the upstream
[WGC BGRA8 frame pool](https://github.com/sskodje/ScreenRecorderLib/blob/v6.5.1/ScreenRecorderLibNative/WindowsGraphicsCapture.cpp#L220),
and the same fixed format remains in
[version 7.0.1](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/ScreenRecorderLibNative/WindowsGraphicsCapture.cpp#L220).
There is no managed HDR/tone-map option in the local 6.5.1 API metadata.
The local Win2D 1.3.2 interface header also exposes no `HdrToneMapEffect` or
`WhiteLevelAdjustmentEffect`; those native Direct2D effects are not assumed to be
available as Win2D managed types.

Microsoft explicitly recommends retaining FP16 throughout HDR acquisition to avoid
overclipping, followed by HDR-to-SDR conversion where needed:
[screen-capture documentation](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture).
The white-level units come from
[DISPLAYCONFIG_SDR_WHITE_LEVEL](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_sdr_white_level).
The color-management ordering (linear scRGB, tone mapping, white-level adjustment,
then sRGB for 8-bit output) is described in the
[Direct2D HDR tone-map documentation](https://learn.microsoft.com/en-us/windows/win32/direct2d/hdr-tone-map-effect).
An image already clipped to BGRA8 has lost highlight detail; applying this function
to such bytes cannot recover that information.
