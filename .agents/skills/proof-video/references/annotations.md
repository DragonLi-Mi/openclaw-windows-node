# Measured Windows screenshot and video annotations

Capture the real OpenClaw Windows surface required by the claim. Synthetic
fixture data can prove supported presentation behavior, but it cannot prove a
real Gateway, provider, installer, permission, or Windows-node capability path.
Keep the raw screenshot or recording, measured rectangles, source identity, and
annotated output together.

## Coordinate system

Use coordinates relative to the complete captured app-window image with a
top-left origin.

- `coordinateSpace: "raw-px"` is the default for native PNG and MP4 evidence.
- `scale` is raw pixels per XAML DIP only when the capture metadata supplies a
  reliable rasterization scale. It is not editor zoom.
- Keep the original frame width and height in the sidecar.
- `OwnedWindowCapture` already stores asserted UI Automation rectangles relative
  to the captured window.
- For `OnboardingNativeProof`, convert XAML bounds with
  `contentBounds - windowBounds + logicalBounds * rasterizationScale`, using
  floor for left/top and ceiling for right/bottom as `ClientPixels` does.
- For another UI Automation source, subtract the captured image's desktop
  origin. A region recording must record and use its region origin, which may
  differ from the app window's origin.
- Reject targets that are missing, offscreen, zero-sized, outside the owned app
  HWND, or cropped by the image.

The first rectangle is the reference for an alignment comparison. Use
`labelAt` to place a label in clear space. Labels are single-line and must fit
the final frame. Shorten text instead of shrinking it until it is unreadable.

## Annotation kinds

| Kind | Fields | Meaning |
|---|---|---|
| `arrow` | `from`, `to`, optional `text` | Pointer between `{x,y}` positions. Stop before covering the target. |
| `outline` | `rect`, optional `text` | Unfilled boundary around `{x,y,width,height}`. |
| `alignment` | `axis`, `edge`, `rects` | Compare `start`, `center`, or `end` coordinates. Report the maximum absolute offset from the first rectangle. |
| `spacing` | `axis`, `from`, `to`, `at` | Measure the gap between ordered rectangles, or their intersecting interval when they overlap. |

Every kind accepts optional `labelAt: {x,y}`. Keep annotation bounds inside the
captured frame.

## Measurement sources

Prefer an existing repository-owned measurement:

1. `OwnedWindowCapture` writes asserted UI Automation bounds relative to the
   captured owned HWND, plus image SHA-256 and window bounds.
2. `OnboardingNativeProof` writes XAML and native content bounds, DPI,
   rasterization scale, source/build identity, image SHA-256, OCR evidence, and
   clipping assertions.
3. Fixture UI tests can write state-specific PNG and JSON artifacts under the
   run artifact directory.

For a manual capture, record the measurement method and window bounds. Do not
estimate a spacing or alignment label by eye and present it as measured.

## Privacy boundary

Annotations and sidecars must follow the selected proof pool's `redact` and
`neverCollect` lists. Do not capture or transcribe tokens, setup codes, API
keys, device identities, private keys, secret-entry fields, authored chat,
session titles, private endpoints, user names, machine names, or profile paths.
Use fixture or disposable test values, and inspect metadata as well as pixels
before publication.

## Screenshot sidecar

Save measured geometry as `<name>.annotations.json` beside the untouched raw
screenshot. Never overwrite the `<name>.json` metadata written by repository
capture helpers.

```json
{
  "format": "openclaw-windows-proof-annotations",
  "version": 1,
  "image": "raw.png",
  "rawSha256": "<sha256>",
  "coordinateSpace": "raw-px",
  "frame": { "width": 1440, "height": 900 },
  "sourceIdentity": {
    "head": "0123456789abcdef0123456789abcdef01234567",
    "dirtySha256": "<sha256>",
    "dirtyFileCount": 4
  },
  "annotations": [
    {
      "kind": "alignment",
      "axis": "x",
      "edge": "start",
      "rects": [
        { "x": 240, "y": 200, "width": 400, "height": 48 },
        { "x": 264, "y": 320, "width": 400, "height": 48 }
      ],
      "labelAt": { "x": 700, "y": 300 }
    }
  ]
}
```

Apply the sidecar in an image editor already installed and authorized for this
task. The repository does not currently own a screenshot annotation renderer.
Preserve the sidecar as the measurement record and never overwrite `raw.png` or
the capture helper's adjacent metadata.

## Video sidecar

Timed marks use the raw video clock:

```json
{
  "format": "openclaw-windows-proof-cues",
  "version": 1,
  "video": "raw.mp4",
  "rawSha256": "<sha256>",
  "sourceIdentity": {
    "head": "0123456789abcdef0123456789abcdef01234567",
    "dirtySha256": "<sha256>",
    "dirtyFileCount": 4
  },
  "coordinateSpace": "raw-px",
  "frame": { "width": 1440, "height": 900 },
  "cues": [
    {
      "kind": "annotation",
      "start": 2.0,
      "end": 6.0,
      "annotations": [
        {
          "kind": "outline",
          "rect": { "x": 240, "y": 200, "width": 400, "height": 48 },
          "text": "Pending reapproval"
        }
      ]
    }
  ]
}
```

Re-measure and replace marks after navigation, scrolling, resize, DPI changes,
theme changes, or layout updates. Clear annotations while the target moves. A
timed mark describes a stationary frame interval, not a live element tracker.

When an authorized editor adds zoom, apply annotations before zoom so they
share the image transform and remain bound to the changed pixels. Keep captions
fixed in the final viewport so narration does not move or crop with the content.

## Comparison captures

For before and after comparisons:

- use the same app surface, theme, window size, DPI, crop, scale, and edge;
- keep the same reference element first;
- record separate source identities for before and after;
- do not reuse an old annotation image after geometry changes;
- label historical evidence with its actual commit and date;
- do not present fixture data as the user's real state.

## Inspection

Compare raw and annotated stills at full size and expected PR display size.
Check that:

- arrowheads indicate the intended target;
- outlines do not cover the evidence;
- alignment and spacing labels agree with saved bounds;
- asserted controls are fully inside the captured app window;
- marks remain correct for their entire timed interval;
- labels remain readable after crop, zoom, and final encoding;
- the raw image or video, sidecar, annotated output, and source identity have
  matching hashes and provenance.

For video, inspect the full timeline and frames at annotation changes, caption
changes, zoom holds, speed boundaries, cuts, and the final result. Reject blank,
near-uniform, cropped, obscured, stale, or privacy-unsafe evidence.
