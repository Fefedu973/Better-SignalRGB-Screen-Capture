# Ambient halo

Choose a style in **Output editor → Ambient halo**. Preview it before enabling the web output or app-controlled SignalRGB appearance. Use **Inset** under Picture placement to leave room around a picture that fills the canvas.

| Style | Behavior |
| --- | --- |
| Contours | Extends nearby edge colors outward, perpendicular to straight edges and around corners. Recommended when preserving the location of screen colors matters. |
| Soft | Combines a near and a far blurred halo for a diffuse ambient wash. |
| Classic | Retains the original dilation and blur. Existing profiles keep their selected style. |

Contours uses the visible composed silhouette after crop, source rotation, mirrors, overlap and global picture placement. Black image content is still part of the silhouette; it is not mistaken for empty canvas. Overlapping sources contribute their already-composited colors, and empty gaps remain outside the silhouette.

## Contours controls

- **Sampling depth** selects how far inside the edge to gather colors, as a percentage of the placed picture's shorter physical dimension. Start at 3%. Sampling stops at a gap instead of jumping to another source.
- **Lateral blend** controls mixing along an edge, independently of reach. Start at 2; use 0 for the sharpest separation between nearby colors.
- **Reach** controls outward extension, in units relative to the 320-wide output. **Fill the output area** extends this range to cover the output.
- **Distance fade** controls brightness falloff. At 0 the halo is uniform up to its reach; larger values darken distant light more strongly.

Saturation, intensity and dark-color cutoff remain available. Saturation 1 preserves the sampled colors; the stored value is never changed automatically when switching style. Cutoff operates on sampled source colors before global picture color filters. Hue, brightness and picture presets also affect the halo. **Soften picture** blurs the picture only in Contours mode, preserving the halo's exterior mask.

The halo is outside the visible sources. With **Hide picture in full-area mode**, their interiors are black rather than filled with invented image content. Sources that touch every edge of the output leave no exterior space; shrink their placement to expose the halo. Letterboxing within an image is not automatically removed: crop those bars or adjust sampling depth deliberately.

## Rendering and maintenance

The web output, embedded preview and matching SignalRGB effect use the same halo implementation. The default renderer uses WebGL when suitable, with a bounded Canvas2D fallback. Image changes reuse the contour geometry; moving, cropping or resizing sources invalidates it. The halo adds no frame queue or temporal smoothing.

The contour mask is limited to a few hundred pixels per dimension (320 × 200 normally, 320 × 240 for 800 × 600 HQ). The accelerated renderer samples the full input picture. This bounds geometry and fallback work, but extremely thin shapes and subpixel edges are approximated at mask resolution. The fallback samples a reduced picture and can look slightly softer. Actual cost depends on browser, GPU and output size; automated timings are measurements of the test machine, not a universal performance guarantee.

`Services/WebOutput/ContourHalo.js` is the canonical implementation. After editing it, run `node scripts/Sync-ContourHalo.cjs` to update the generated block in the standalone effect. `node scripts/Sync-ContourHalo.cjs --check` and the effect tests reject stale copies. Existing external SignalRGB HTML installations need the new bundled effect to understand Contours.

Run `node tests/ContourHaloTests.cjs` for directional color, independent crop/rotation oracle, fallback, lifecycle and timing checks. The complete browser suite also compares the actual web page with the SignalRGB effect, including live changes to an unchanged source image.
