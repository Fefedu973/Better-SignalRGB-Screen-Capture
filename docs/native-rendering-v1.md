# Native rendering metadata v1

This is the Better-side rendering contract for the Room fork. Connection, authentication, leases, scene acknowledgements and recovery are specified in [the native API contract](native-openrgb-integration-v1.md). The receiver still has to implement and validate its native renderer. These fixtures do **not** prove an operational OpenRGB/Effects integration.

The authoritative implementations are `Services/NativeOutput/NativeRenderMetadata.cs`, `Services/WebOutput/StreamingCanvasPage.js`, `Services/WebOutput/StreamingCanvasPage.html`, and the shared `Services/WebOutput/ContourHalo.js`. The audited base revision was `ff335ac86fbf9e8f72f17c87113e2e5753a64606`; this contract adds native metadata to that application rather than changing the web renderer.

## Three different inputs

| Input | Meaning | What it must not be used for |
| --- | --- | --- |
| Image ORGBFRM1 surface | Raw composed image, opaque BGRA8 sRGB, black outside all sources, before appearance and global screen placement | Inferring coverage from non-black pixels; assuming the halo is already applied |
| Coverage ORGBFRM1 surface | `B=G=R=round(255*A)`, alpha **255**, where A is the composite's accumulated source-opacity coverage; same dimensions as image | Treating transport alpha as coverage; inferring the Contours silhouette from luminance |
| Rendering metadata | Source polygons, transforms, presence, opacity, ordering, effective appearance | Pairing it with a different raw frame generation; assuming source pixels can be recovered independently from the flattened image |

All frames retain ORGBFRM1's opaque format. No reserved header field is repurposed. Coverage is a second ordinary opaque surface, not a changed image protocol. Geometry distinguishes a solid black image from absent capture. Transparent source composition has `A = 1 - product(1 - sourceOpacity * rasterCoverage)`; source JPEGs themselves are opaque.

For a matching image/coverage pair, image RGB is already color composited over black. Reconstruct an approximate transparent scene as `A=coverage.r/255`, `unassociatedRGB=A>0 ? clamp(rawRGB/A,0,1) : 0`. A premultiplied RGBA working texture can instead keep raw RGB and set alpha to A. Do not divide twice. Filter operations which require unassociated color must unpremultiply before their color matrix, then premultiply again for spatial filtering/composition. Eight-bit rounding is amplified at very low opacity; missing pre-quantization precision cannot be reconstructed.

The coverage surface is rasterized by Better's native compositor using its transforms/clips and opacity. It is tied to image state, not JPEG contents, and remains valid for static or changing source pictures with the same geometry/presence. Contours instead uses the **opaque union** of polygons from contributing sources, regardless of their nonzero opacity. These two masks deliberately differ.

## State/frame association

Use `GET /api/native/v1/states/{rawGeneration}` from the authenticated loopback API. Its immutable published-state envelope contains `stateRevision`, `controlRevision`, `scene`, `rendering`, `image`, and `coverage`. Generation and sequence in transport references are decimal **strings** to preserve 64-bit precision in JSON clients. The embedded rendering record's `stateRevision` identifies this effective geometry/appearance state.

1. Read an image under the ORGBFRM1 Reader's mutex and retain its generation and sequence.
2. Obtain the immutable state for that **raw generation**, not just the current status.
3. Check that the image sequence is at least the state's first valid `image.sequence`, with matching generation, dimensions and format.
4. Read the coverage channel and require the exact referenced generation and sequence. Its sequence is fixed for this state, initially 1.
5. Render only after both checks. If a fixed channel has already advanced to another generation, discard this incomplete pair and retry from step 1. Never combine an old raw image with the latest geometry.

Better publishes coverage before the first image and announces a new state only after both publications succeed. Geometry, dimensions, effective appearance or contributing-frame identity changes retire the previous publisher generations. Ordinary new source pixels in the same state increase the image sequence without rebuilding coverage. Current status reports the latest sequence; an immutable state's image reference intentionally names its first valid sequence. Keep only bounded cached states/frames. Reader lifetime/owner checks distinguish an unchanged static picture from a dead producer; frame age alone does not.

## Rendering JSON record

The `rendering` object is serialized camelCase:

```json
{
  "version": 1,
  "schema": "better.native-rendering",
  "stateRevision": 7,
  "canvasWidth": 320,
  "canvasHeight": 200,
  "outputWidth": 800,
  "outputHeight": 600,
  "effectiveSettings": { "version": 1, "pictureMode": "Standard" },
  "sources": []
}
```

The example abbreviates settings; actual responses include every field below. Source IDs are UUID strings. Do not use names, array indices or process/window handles as identities. Source-specific URLs and credentials are not included in rendering metadata.

Coordinates have top-left origin, X right, Y down. Positive rotation is clockwise in this coordinate system. Geometry remains in logical **320×200**, including HQ **800×600**. Scale output axes independently: `(x*outputWidth/320, y*outputHeight/200)`. HQ is not a letterbox or an aspect-preserving 16:10 picture. Source records, sorted back-to-front, contain:

| Fields | Semantics |
| --- | --- |
| `id`, `type`, `order`, `zIndex` | Stable ID/type; explicit consecutive paint order. Equal zIndex retains input order. Paint by `order`. |
| `hasFrame`, `contributes` | A current decoded frame exists; and that frame has nonzero opacity and nonempty canvas coverage. Only contributing sources enter the silhouette/coverage. Geometry can remain listed while a frame is missing. |
| `canvasX/Y/Width/Height`, `rotation`, `opacity` | Source rectangle before rotation; rotation about its full rectangle center; opacity 0..1. These are independent of global screen placement. |
| `isMirroredHorizontally/Vertically` | Mirror media within its source rectangle. They do **not** mirror crop placement. |
| `cropLeft/Top/Right/BottomPct`, `cropRotation` | Despite the name, margins are fractions 0..1, not values 0..100. Rotate the crop rectangle around its **own** center, then intersect it with the image rectangle. |
| `canvasFromImage` | Affine transform from source-sized local image coordinates: `x'=m11*x+m21*y+dx`, `y'=m12*x+m22*y+dy`. Includes media mirroring and source rotation/translation. Texture UV first scales by source width/height. |
| `cropPolygonLocal` | Four crop corners after independent crop rotation, in non-mirrored source-local coordinates; corners may extend outside the source rectangle. |
| `coveragePolygon` | Convex polygon of image∩crop, rotated/translated to the canvas, clipped to `[0,320]×[0,200]`. Empty means invisible. Mirroring media leaves this polygon unchanged. |
| `placedCoveragePolygon` | The previous **already clipped** polygon after global screen XYWH placement, again clipped to the canvas. Use for placed Contours geometry, not for the raw image surface. |

Do not apply `canvasFromImage` to `coveragePolygon`: its points are already in canvas space. Do not transform `cropPolygonLocal` by the media mirror; either use the provided coverage polygon or apply source rotation/translation without the mirror. Web snapshot CSS contains a compensating inverse reflection in its crop polygon because its outer element mirrors media and clip together. The native polygon schema removes that implementation detail.

The pure factory rejects duplicate IDs, more than 128 sources, non-finite geometry, invalid dimensions and unnormalized opacity/crops. Source polygons are analytical doubles; native/web rasterizers may antialias their edges differently. Opaque overlap is a geometric union, not XOR, and a fully covered lower source must not create an interior silhouette edge.

## Effective settings: actual defaults and limits

| Field | Default | Range or values |
| --- | --- | --- |
| `version` | 1 | 1 |
| `enabled`, `webEnabled` | false, false | Independent SignalRGB/web booleans; neither implies native output is disabled |
| `screenX`, `screenY` | 0, 0 | 0..320-width; 0..200-height in the app |
| `screenWidth`, `screenHeight` | 320, 200 | 1..320; 1..200 |
| `pictureMode` | Standard | Standard, Cinema, Mono, Vivid, Dominant, HD |
| `hue` | 0 | -180..180 degrees |
| `brightness`, `saturation` | 0, 0 | -100..100; CSS factor `(100+value)/100` |
| `blur` | false | Picture Gaussian blur of 1 logical CSS pixel when true |
| `ambilight` | true | Master appearance halo switch |
| `ambilightStyle` | Classic | Classic, Soft, Contours |
| `ambilightCutoff` | 0 | 0..100; zero bypasses the cutoff mask |
| `ambilightFullscreen`, `hideSources` | false, false | Picture is hidden only when halo and fullscreen are both enabled |
| `ambilightBlur`, `ambilightSpread` | 30, 10 | 0..100 logical pixels; Classic/Soft only |
| `ambilightSaturation` | 3 | 0..10 color saturation factor |
| `ambilightIntensity` | 100 | 0..200 percent RGB multiplier |
| `ambilightEdgeDepth` | 3 | 1..20 percent of the smaller placed-picture extent |
| `ambilightEdgeMix` | 2 | 0..30 percent, tangential sample displacement |
| `ambilightEdgeReach` | 60 | 1..200 units relative to a 320-wide output |
| `ambilightEdgeFade` | 50 | 0..100; zero has uniform strength inside reach |
| `interpolation` | smooth | smooth, pixelated |
| `frameRate` | 15 | 1..30 web/effect render cadence; consumer cadence and native capture rate are separate |

`SignalRgbEffectSettings.Normalize()` supplies/clamps these values. Invalid enum strings fall back to Standard/Classic/smooth; non-finite placement and halo saturation fall back to defaults. The standalone SignalRGB host permits partially off-canvas global placement; application-native settings use the bounded application placement above. Native consumers wanting their own placement may do so locally without altering Better preferences.

## Order and color conventions

Source pixels are sRGB-encoded JPEG colors. The web canvas defaults to sRGB, and both SVG halo filters explicitly set `color-interpolation-filters="sRGB"`. Reproducing this behavior means **not** silently changing all arithmetic to linear-light lighting. A receiver may offer linear-light as a different artistic mode, but it is not the v1 reference. Use premultiplied alpha for over/compositing and spatial filtering; CSS color-filter matrices act on unassociated color with the required clamping. Hue rotation is the CSS hue-rotate matrix, not an HSV/HSL hue replacement.

1. Decode each source, resize it to its full local rectangle (smooth or nearest), apply image mirror, independent crop clipping, source rotation/translation and opacity, paint back-to-front into a **transparent** scene clipped to 320×200. The bitmap can be 800×600 without changing geometry.
2. The picture color chain is `hue-rotate(hue)` → `brightness(1+brightness/100)` → `saturate(1+saturation/100)` → picture-mode chain → optional `blur(1px)`.
3. Modes append: Standard nothing; Cinema `sepia(.2) contrast(1.1) brightness(.9)`; Mono `grayscale(1)`; Vivid `contrast(1.3) saturate(1.4) brightness(1.1)`; Dominant `contrast(1.1) saturate(1.2)`; HD `contrast(1.05) saturate(1.1) brightness(1.02)`. **Dominant currently means this fixed filter chain**, not dominant-color clustering.
4. Place the picture using global XYWH. Local Classic/Soft filter the resulting placed, color-filtered picture group and merge the original picture over its light. Fullscreen Classic/Soft filter a separate copy of the full scene stretched to the whole output, independent of inset picture placement; then draw the placed picture above it unless hidden.
5. Contours samples the **unfiltered** transparent composition, applying placement to both color lookup and silhouette. Its cutoff/saturation/intensity precede the general tone chain. Apply hue/brightness/saturation/picture-mode to the completed halo; **exclude picture blur** from this halo chain to preserve the exterior-only mask. The picture still uses the normal chain including blur. Finally composite halo behind the picture onto black.

### Classic and Soft

Let `Y=.2126R+.7152G+.0722B`, `c=cutoff/100`. For cutoff>0, multiply glow-source alpha by `clamp(5*(Y-c),0,1)` while retaining its unassociated RGB. At cutoff=0 bypass this mask entirely, preserving semitransparent opacity. Cutoff never changes the visible picture.

Classic: glow-source → per-channel morphology **dilate** of radius spread → saturation → Gaussian sigma blur → intensity RGB factor. Dilation is not a radial nearest-edge projection and can synthesize colors through channel-wise maxima.

Soft: saturate glow-source; blur two copies with `near=max(.01, blur*.35+spread*.15)` and `far=max(.01, blur+spread*.5)`; combine `0.65*near+0.35*far`; multiply RGB by intensity/100. No morphology. Local output merges unmodified SourceGraphic over the light; fullscreen output contains light alone. Filter bounds extend by `ceil(spread+3*blur)` for Classic and `ceil(3*max(near,far))` for Soft. Browser Gaussian implementations/edge antialiasing are reference-raster details, not a promised bit-exact cross-GPU kernel.

### Contours

Port the shared `ContourHalo.js` algorithm, not an eight-patch rectangular approximation. Use the opaque union of contributing source polygons after **capture-canvas clipping and global placement**. Black source content is inside this mask.

- Rasterize geometry to width min(320,outputWidth); height min(320,round(maskWidth*outputHeight/outputWidth)). Normal/HQ masks are 320×200/320×240. Geometry uses 50% alpha coverage for occupancy; retain original antialias coverage for exterior attenuation.
- Cache an exact squared Euclidean nearest-boundary field. Project an exterior point to its nearest visible boundary: orthogonal to straight sides, radial about corners. Do not project from the canvas center. Rebuild only for geometry/presence/placement/dimension changes.
- Distance metric is `(320,320*outputHeight/outputWidth)`. This preserves physical orthogonality after HQ's 4:3 stretch. Image placement coordinates remain 320×200. Let `L=min(screenWidth,screenHeight/200*metricHeight)`; band depth is `max(.5,L*edgeDepth/100)`, lateral displacement `L*edgeMix/100`.
- Sample four positions through the interior band, for the center and optional two tangent-offset bands. Traverse all crossed occupancy cells when determining band length: stop at the first empty cell. Sparse color taps must not jump a narrow gap and sample an unrelated source. Cache these limits for geometry/depth/mix changes. Both shader and limit atlas use the field-cell-center normal; the actual output coordinate determines distance/fade.
- Accumulate source RGB weighted by source alpha and the same cutoff function above. Average alpha over valid taps. Apply halo saturation about sRGB luminance and intensity to averaged unassociated RGB. No artificial temporal smoothing.
- Outside-mask alpha factor is `1-maskAlpha`. For `d<reach`, fade is `1` at edgeFade=0, otherwise `(1-d/reach)^(4*edgeFade/100)`; outside reach emit nothing. Fullscreen replaces reach with the metric diagonal. Interior emits no halo: fullscreen+hideSources leaves source silhouettes black, deliberately.
- The bounded grid approximates sub-grid holes/edges. Exact nearest-field ties are deterministic. Eight-bit packed sampling limits and tiny inward safety margins prevent stepping across a represented gap. All-opaque masks can skip halo rendering completely.

The shared engine has a GPU path without per-frame image readback and a bounded Canvas2D fallback. Software WebGL is intentionally sent to Canvas2D. These are implementation options for the native agent, not requirements to embed JavaScript or a WebView.

## Fidelity boundaries of raw native output

An opaque raw image by itself is insufficient for faithful appearance: a contrast/sepia/blur on black-flattened pixels treats added background as real content. Matching opacity coverage makes approximate transparent reconstruction possible and supports the global appearance chain, because web picture filters occur **after source composition**, not separately per source.

That reconstruction cannot undo GDI versus Canvas2D rasterization, native Bilinear/HighQualityBicubic versus browser interpolation, quantization, edge rounding, or the source JPEG encode/decode already present in the initial native path. Native raw composition currently does not switch its source-resize kernel according to the appearance setting `interpolation`; setting pixelated downstream cannot reconstruct source pixels already smoothed by GDI. Per-source native surfaces would be a future stronger-fidelity path, not an implemented capability of v1. The receiver must not advertise pixel-identical browser output from this flattened stream.

Contours geometry and filter semantics can be reproduced, including rotated/mirrored crops, with the metadata and coverage above. Reference scene RGBA PNGs isolate the native GLSL appearance stage from those upstream composition differences. Compare native shaders first against those reference scene/coverage inputs, then separately measure end-to-end native GDI differences. Do not apply global appearance twice to a raw image.

## Synthetic references and checks

`tests/fixtures/native-rendering-v1/cases.json` contains thirteen scenes, complete production metadata/settings and source snapshots. `source-black.jpg` and `source-quadrants.jpg` are synthetic opaque JPEGs shared by all cases; the latter includes muted colors and grayscale bars so tone filters cannot pass merely by clipping saturated primaries. No screen capture, window identity, URL, device ID or personal configuration is included.

For every case:

- `*-scene.png`: actual production web canvas **before** picture placement, tone and halo, RGBA with transparency. This is the strongest starting input for isolating GLSL appearance correctness.
- `*-coverage.png`: the scene alpha copied into R/G/B with transport-compatible alpha255; includes source opacity and overlap. This is a **browser** coverage reference, not a claim that GDI's antialiased coverage is identical.
- `*-silhouette.png`: actual production `drawContourMask`, with white covered regions and transparent exterior, independent of source opacity. Apply global placement after this capture-canvas clipping.
- `*-appearance.png`: production web page's final visible output on black at its declared output dimensions. Includes effective color/halo/placement; default-headless Chromium may use the engine's CPU fallback.

`reference-manifest.json` records SHA-256 hashes, dimensions and Chromium version. Cases cover black-content coverage, asymmetric mirrored crop, independent crop/source rotations off-canvas, semitransparent overlap, all six picture modes, Classic inset, Soft fullscreen, Contours HQ/fullscreen with hidden picture, and a listed source without a frame. More renderer-specific cases are in `ContourHaloTests.cjs`, `WebOutputPageTests.cjs` and `StreamingEffectBrowserTests.cjs`.

Run from the repository root:

```powershell
dotnet run --project tests/BetterSignalRGB.NativeRenderingTests/BetterSignalRGB.NativeRenderingTests.csproj
node tests/NativeRenderingReferenceTests.cjs
```

Intentional reference regeneration (review both changed metadata and image diffs):

```powershell
dotnet run --project tests/BetterSignalRGB.NativeRenderingTests/BetterSignalRGB.NativeRenderingTests.csproj -- --export tests/fixtures/native-rendering-v1/cases.json
node tests/NativeRenderingReferenceTests.cjs --update
```

Node tests require Playwright and its Chromium runtime. The C# suite currently checks 8,018 assertions, including 8,000 sampled points against an independent inverse-transform image/crop-membership oracle. The browser test compares polygons to the existing renderer's mask (zero interior disagreement; measured antialias mean error at most 0.547/255, bounded below 1/255), independently checks opacity accumulation, and compares all 52 reference PNGs with bounded cross-runtime raster tolerance. Intersecting two antialiased clipping paths and rasterizing their analytical intersection once can differ along the edge. These are synthetic correctness tests, not capture/transport/GLSL benchmarks. Transport/composition benchmarks must use the same scene and cadence at both resolutions and separately report source decode, composition, publication, reader copy and GPU upload; this document claims no new latency measurement.
