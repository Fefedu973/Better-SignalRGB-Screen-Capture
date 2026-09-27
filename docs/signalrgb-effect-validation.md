# SignalRGB effect rendering and validation

The effect now renders one 320 x 200 Canvas2D scene and copies that exact scene for
full-area ambilight. Each source uses its source-center rotation, media mirror,
full-source bounds and transmitted rotated crop polygon in the same order as
`CompositeFrameService`. `StreamingSourceSnapshot` compensates the polygon for
the media mirror, so mirroring cannot move the crop mask. Sources retain their
stacking order and opacity. Full-area glow no longer stretches or rotates each
source independently to a nearest-source ownership region.

Picture mode, hue, brightness, saturation and optional image blur form one filter
chain. That chain also applies to full-area glow when the source picture is hidden.
Both local and full-area ambilight honor spread, blur, saturation and intensity.
Image interpolation and maximum raster frame rate are configurable. Source-picture
placement controls retain their existing SignalRGB names and behavior.

App settings arrive as `config:` followed by a version-1 JSON object. Invalid JSON
or unsupported versions leave the current settings intact; values are validated and
clamped. The `enabled` flag controls an override kept separate from the SignalRGB
globals. Turning it off immediately restores the host's settings. Identical config
heartbeats do not cause redraws. Resetting sources does not reset these preferences.

An independent `health:` version-1 message supplies a loopback callback and a random
session token. The effect reports its draw counter at most once per second, with
one request in flight and a bounded timeout. Only the exact local status route is
accepted. Reset stops feedback; the server rejects expired or mismatched tokens.
The setup panel can therefore distinguish API reachability, an active matching
effect and at least one rendered image. This is not a physical LED confirmation.

The effect keeps only the latest pending complete frame per source, with at most
two image decoders active globally. Superseded pending frames are released before
decode. The previous valid frame stays visible if the next JPEG is invalid. Partial
assemblies and stalled decodes expire after five seconds; source removal also
invalidates late decoder callbacks. Complete paused frames do not expire. Limits:
128 sources, 2,048 chunks/frame, 12 MiB base64 characters/frame, 32 MiB total retained
base64 characters and 16 Mi pixels across decoded retained source images. These
bounds exclude transient browser decoder/GPU allocations. Geometry paths are
cached; unchanged JPEGs and unchanged scenes are not decoded or rasterized again.
At the encoded budget limit, already decoded images can be detached into 1:1 memory
canvases without filters or interpolation. Their base64 strings and image data URLs
are then released while the previous pixels remain visible, including when the
replacement JPEG is invalid. This pressure-only copy permits existing sources to
keep updating without evicting another source. Exact encoded-frame deduplication is
lost for a detached image until its next successful replacement; no hash is retained.

## Tests

```powershell
node tests/StreamingEffectTests.cjs
dotnet run --project tests/BetterSignalRGB.StreamingTests -c Release
npm ci --prefix tests --ignore-scripts
Push-Location tests
npx playwright install chromium
Pop-Location
node tests/StreamingEffectBrowserTests.cjs
```

The browser suite requires Playwright and Chromium, installed by the explicit setup
commands above and pinned in `tests/package-lock.json`. The test itself does not
install dependencies. If Playwright is outside the project dependency
tree, set `NODE_PATH` to its containing `node_modules` directory. On the validated
workstation it is the bundled Codex runtime dependency directory. The suite starts
its own headless Chromium process and closes it; it never uses the user's browser.

The browser suite runs the production C# compositor harness with
`--export-effect-fixtures <path>`. Eleven synthetic colored-image cases are generated
under its ignored `obj` directory, including asymmetric crop, different source/crop
rotation angles, horizontal/vertical/both mirrors, negative positions, canvas-edge
clipping, multiple overlapping sources and partial opacity. Cardinal crop angles
also exercise scientific notation for tiny nonzero
coordinates from invariant floating-point serialization. No desktop/user images
are captured or stored. Chromium raster results are compared both with production
JPEG composite output and an independent mathematical inverse-transform oracle.

On Windows/Chromium, all eleven cases passed. Mean absolute RGB differences from the
production JPEG composites were 1.665 to 2.438 out of 255; the differences include
GDI+/Canvas edge rasterization and the compositor's additional lossy JPEG encoding.
The independent oracle found zero mismatches among 34,046 sampled pixels away from
edges. The unfiltered full-area glow and visible scene had identical pixels.
Actual browser screenshots additionally verify that the final hidden-source,
full-area glow is monochrome with image blur enabled, and that zero glow intensity
produces a black result. These synthetic screenshots stay in memory. The Node
and browser suites both exercise a saturated 32 MiB encoded budget, invalid-frame
replacement with unchanged visible pixels, and subsequent recovery without reset.
An actual browser fetch to a test-owned loopback HTTP server also verifies the
render counter and active session token, rather than merely mocking the callback.
The Node suite separately validates assembly limits, global decode and pixel budgets,
latest-frame replacement, stale callbacks, settings overrides, filter composition,
frame-rate throttling and unchanged-frame/geometry reuse.

These checks validate browser rendering and the production transport contract, not
a physical SignalRGB installation or final LED hardware output. SignalRGB's named
property callbacks, effect metadata and `onCanvasApiEvent` sender contract remain
available for that final host-level check.
