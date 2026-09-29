# PhotoEditor – Plan

A simple Lightroom-style photo editor: light and color adjustments, masks (brush, gradients, AI selection), AI denoise/sharpen.

## Stack
- .NET 10, Avalonia 12, CommunityToolkit.Mvvm
- SkiaSharp 3.119 (display, JPEG/PNG/WebP, GPU runtime shaders for live preview); same version Avalonia.Skia uses
- Magick.NET (LibRaw inside) for camera RAW (CR3, CR2, NEF, ARW, DNG, …); TIFF / HEIC – later
- ONNX Runtime + DirectML (AI models) – later
- xUnit test project (`tests/PhotoEditor.Tests`) for the image math in `src/PhotoEditor.Core` (UI-free library)
- Avoid ImageSharp (commercial license restrictions) and Ultralytics YOLO (AGPL)

## Principles
- Non-destructive: the original image is never modified; edits are a list of settings applied on render.
- Live preview on a downscaled image via GPU shaders; full resolution only on export.
- Masks are grayscale images; every mask type (brush, gradient, AI) produces the same kind of mask.

## Phases
Each item is meant to be one small, self-contained step.

### Phase 1 – Viewer, adjustments, export
- [x] Project setup (Avalonia MVVM, git)
- [x] Open image (file dialog + drag & drop), show in viewer
- [x] Zoom (fit / 100% / mouse wheel) and pan
- [x] Adjustment pipeline: settings model + SkSL shader rendering on a preview-sized image
- [x] Light: exposure, contrast, highlights, shadows, whites, blacks
- [x] Color: temperature, tint, saturation, vibrance
- [x] HSL panel: hue / saturation / luminance per color band (reds, oranges, yellows, greens, aquas, blues, purples, magentas)
- [x] Before/after toggle, reset per slider (double-click) and reset all
- [x] Export full resolution (JPEG quality, PNG), keep EXIF

### Phase 2 – Edit stack
- [x] Undo / redo
- [x] Save/load edit settings as sidecar file (.json next to the image)

### Phase 3 – Masks
- [x] Mask model: list of masks, each with its own adjustment set; add/subtract/intersect components
- [x] Mask overlay display (red tint) and mask list panel
- [x] Brush (size, feather, flow, erase)
- [x] Linear gradient (on-canvas handles)
- [x] Radial gradient (ellipse, feather, invert)
- [x] Vignette (amount, midpoint, roundness, feather)

### Phase 3b – Requested additions
- [x] Camera RAW support (Canon CR3/CR2, NEF, ARW, DNG, …) via Magick.NET / LibRaw
- [x] Crop tool (crop rectangle, aspect ratio presets, straighten angle)
- [x] Lightroom-compatible edits: write/read Adobe Camera Raw XMP (`crs:` settings) so Lightroom can open the edits
- [x] XMP sidecars for every format (JPEG, PNG, DNG too); originals are never modified, export never overwrites the source
- [x] Auto button (classic, non-AI): suggest Light sliders, white balance and vibrance from the photo's statistics

### Phase 4 – AI masks
- [x] ONNX Runtime integration (DirectML with CPU fallback), model download on first use
- [ ] Click / box to select (MobileSAM or EfficientSAM)
- [ ] Select Subject (BiRefNet-lite)
- [ ] Select Sky / People (SegFormer)

### Phase 5 – AI enhance
- [ ] Denoise (NAFNet or SCUNet), tiled processing with progress
- [ ] Defocus deblur / sharpen (Restormer or NAFNet), strength blending
- [ ] Classic sharpening (unsharp mask) in the normal pipeline

## Where I left off
- 2026-09-29: Open image done. New `PhotoEditor.Core` library (UI-free: `ImageLoader` decodes via SkiaSharp and applies EXIF orientation) + `tests/PhotoEditor.Tests` (xUnit). `ImageViewer` control draws an SKImage through Avalonia's Skia lease (custom draw op), fit-to-window. Open via button / Ctrl+O / drag & drop / command-line arg. Dark theme. Screenshots under Xvfb on Linux work (apt `xvfb x11-apps imagemagick`). Next: zoom & pan.

- 2026-09-29: Zoom & pan done. `Core/Viewing/ViewTransform` (tested) holds scale/offset; viewer: wheel zooms at cursor, left/middle drag pans, double-click toggles fit/100%, Fit/100% buttons, Ctrl+0 / Ctrl+1; zoom % in status bar. Verified under Xvfb. Next: adjustment pipeline (settings model + SkSL shader on preview-sized image).

- 2026-09-29: Adjustment pipeline done. `Core/Adjustments`: immutable `AdjustmentSettings` record, `AdjustmentParameter` descriptors (UI sliders are generated from `AdjustmentParameters.All`), `PreparedAdjustments` (derived constants), `AdjustmentShader` (SkSL, works in linear light: sRGB decode → adjust → sRGB encode) and `CpuAdjustmentRenderer` (same math in C#, parallel rows, for export). `ShaderParityTests` run the SkSL on Skia's raster backend and compare with the C# output (≤2/255). `PreviewImage` holds full-res + ≤2560px preview; the viewer uses the full image only when zoomed past the preview's resolution. Only Exposure exists so far. Next: Light sliders.

- 2026-09-29: Light sliders done. After exposure (linear), a tone curve (`Core/Adjustments/ToneCurve`) runs on perceptual luminance (Y^(1/2.2)) and is applied to RGB as a ratio so hue is kept: contrast = S-curve around 0.5, highlights = shift weighted by smoothstep(0.35..1) (negative recovers over-exposed values), shadows = bump peaking at 1/3, whites/blacks = x⁴ / (1-x)⁴ end shifts. Tests check monotonicity and shader parity. Strengths are my own guesses, not calibrated to Lightroom. Next: Color (temperature, tint, saturation, vibrance).

- 2026-09-29: Color sliders done. Temperature/tint = linear RGB gains (luminance-normalised) applied with exposure; vibrance (weighted by 1 − current saturation) and saturation scale chroma around luminance after the tone curve. No skin-tone protection in vibrance. Next: HSL panel.

- 2026-09-29: HSL panel done. `HslBand` record per band (8 properties on `AdjustmentSettings`), `HslMath` (C#) / `applyHsl` (SkSL, uniform arrays): HSV on gamma-2.2-encoded values, band adjustments blended with smoothstep between band centres (0,30,60,120,180,240,270,300°); hue ±30°, saturation ×(1±1), luminance ±1.5 stops scaled by saturation (greys unaffected). Panel groups are now Expanders (HSL collapsed by default). Next: before/after toggle, per-slider reset, reset all.

- 2026-09-29: Before/after + resets done. `Before` toggle button and `\` key (viewer renders `DisplaySettings` = Default while on); double-click on a slider or its label resets it (window-level DoubleTapped handler with handledEventsToo, finds the `ParameterViewModel` DataContext); `Reset all` button. Verified under Xvfb. Next: export full resolution (JPEG quality, PNG), keep EXIF.

- 2026-09-29: Export done (Phase 1 complete). `Core/Export/ImageExporter` renders the full-res original with `CpuAdjustmentRenderer` (~1.8 s for 24 MP on 4 cores), encodes JPEG (4:4:4 at quality ≥ 90) or PNG by extension, writes via temp file. `Core/Imaging/ExifMetadata` reads EXIF from JPEG APP1 / PNG eXIf / WebP EXIF, sets orientation to 1 (pixels are already upright) and embeds it into JPEG (APP1) or PNG (eXIf). Not handled: ICC profiles (everything is treated as sRGB), stale EXIF thumbnail / pixel-dimension tags, EXIF blocks > 64 KB in JPEG are dropped. UI: Export… button, Ctrl+E, JPEG quality box; export runs on a background thread. Next: Phase 2 undo/redo.

- 2026-09-29: Undo/redo done. Generic `Core/Editing/EditHistory<T>` over immutable states; changes with the same key within 1 s coalesce (one slider drag = one step), capacity 500. All edits go through `MainViewModel.ApplyEdit(settings, key)`; opening an image resets history. Undo/Redo buttons, Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z. When masks arrive, the history type should become a combined edit-state record. Next: sidecar .json save/load.

- 2026-09-29: Sidecar done (Phase 2 complete). `Core/Editing/SidecarFile`: `photo.jpg` → `photo.jpg.json`, camelCase JSON `{ version: 1, adjustments: {...} }` via System.Text.Json; unknown fields ignored, missing = default, out-of-range values clamped; atomic write via temp file. Auto-save 500 ms after the last edit / undo / redo, flushed on opening another image and on window close; Ctrl+S saves now. No file is created for an unedited image. Loaded on open (status shows "edits loaded"). Next: Phase 3 mask model.

- 2026-09-29: Mask model done. `Core/Masks`: `Mask` record (Id, Name, Enabled, own `AdjustmentSettings`, `ImmutableList<MaskComponent>`), abstract `MaskComponent` (Mode Add=max / Subtract=×(1−c) / Intersect=×c, Invert; renders coverage from normalised coords), `MaskRasterizer` (→ float / Gray8), `MaskImageCache` (preview masks re-rasterised only when the component list instance changes). `Core/Editing/EditState` (global adjustments + masks) is now what history, sidecar (`masks` array), export and viewer use. Rendering: global pass, then per active mask a second shader pass `mix(prev, adjust(prev), mask)` chained as nested SkSL shaders; CPU renderer mirrors it (parity tests with masks). No concrete component types yet (tests use their own), so no mask UI; `[JsonPolymorphic]` + `[JsonDerivedType]` must be added on `MaskComponent` together with the first real component. Next: mask overlay (red tint) and mask list panel.

- 2026-09-29: Mask panel + overlay done. Right panel "Masks": New mask / Delete / Whole image, list with enable checkbox, name box, "Show overlay (O)", component list (mode combo — disabled for the first component, Invert, ✕). Selecting a mask makes all sliders edit that mask's adjustments ("Editing mask: …" label). Overlay: `Core/Masks/MaskOverlay` SkSL draws the selected mask as 50% red over the image. Mask ops are pure functions in `Core/Editing/EditStateOperations` (tested). Plain-key shortcuts (\, O) are ignored while a TextBox has focus. "New mask" creates an empty mask for now; the brush/gradient items will add "New brush/linear/radial" buttons. Next: brush.

- 2026-09-29: Brush done. `Core/Masks/BrushComponent` (first real component, registered for polymorphic JSON as "brush"): strokes with radius (fraction of the long side), feather, flow, erase and normalised points; each stroke = round brush swept along the polyline (distance to segment, max within a stroke), composited with flow (strokes build up; erase multiplies down). Preview masks are rasterised at ≤ 1600 px (`MaskImageCache.MaxPreviewMaskSize`, ~11 ms per update), export at full resolution. UI: "Brush (B)" toggle with Size/Feather/Flow/Erase, left-drag paints (Alt = erase), middle/right-drag pans, circle cursor; the first stroke without a selected mask creates one (selected when the stroke ends). One stroke = one undo step (coalesced). Sliders/component list only refresh when what they show changes. Found under Xvfb: software rendering of the shader chain is slow (seconds of lag after painting); expected to be fine with a GPU but not verified. Next: linear gradient.

- 2026-09-29: Linear gradient done. `Core/Masks/LinearGradientComponent` (JSON "linear"): full effect before Start, smoothstep fade to 0 at End, projection in pixel space; `DragHandle` (Start/End/Move) tested. `Core/Editing/EditTool` (None/Brush/LinearGradient/RadialGradient) drives the viewer (`Tool`); VM `ActiveTool` with `IsBrushActive` / `IsLinearGradientActive`. "Linear (L)" toggle: drag on the image creates a gradient in the selected mask (or a new one), then the tool switches off and the handles (start, centre = move, end; perpendicular guide lines) of the selected component (component list is now a ListBox) can be dragged; a click without drag gives a default 25 % gradient. Esc = no tool. One creation/drag = one undo step. Next: radial gradient (viewer code for it was drafted and removed to keep this commit to one item).

- 2026-09-29: Radial gradient done. `Core/Masks/RadialGradientComponent` (JSON "radial"): centre + RadiusX/RadiusY (fractions of image width → equal radii = circle), feather (smoothstep from inner radius), effect inside (Invert = outside); handles: centre (move) + left/right/top/bottom (resize one radius); drag-to-create sets both radii from the centre, a click gives 0.2 × 0.15. "Radial (R)" toggle; per-component Feather slider in the component list. No rotation (Lightroom can rotate the ellipse) — possible later. Next: vignette.

- 2026-09-29: Vignette done — Phases 1–3 complete. Vignette is part of `AdjustmentSettings` ("Vignette" panel group: Amount ±100 = ±2 stops at full weight, Midpoint 0–100 (default 50), Roundness −100 rectangular / 0 follows frame / +100 circle, Feather 0–100 (default 50)); `Core/Adjustments/VignetteMath` + shader (`imageSize` uniform), parity-tested. It is relative to the full image (there is no crop yet). Because masks reuse `AdjustmentSettings`, a mask can also carry a vignette (applied inside the mask) — harmless but maybe hide that group when a mask is selected. Next: Phase 4 (AI masks) — not started, per instructions for this session.

- 2026-09-29: RAW support done (user request: CR3). `Core/Imaging/RawImageLoader` decodes with Magick.NET-Q16 14.17 (LibRaw: camera white balance, sRGB, pixels already upright) into the same 8-bit RGBA bitmap as JPEGs; `ImageLoader` routes RAW extensions there. A 21 MP CR2 takes ~4–5 s, so opening is now async (`OpenFileAsync`, decode + preview on a background thread). Export: RAW files have no EXIF block we can copy, so a minimal one (make, model, date taken, ISO, shutter, aperture, focal length) is built from LibRaw's metadata via `MagickImage.Ping` (fast) — no lens name / GPS. Verified with a real Canon 5D Mark II CR2 (decode + export + EXIF); CR3 goes through the same LibRaw path (Magick lists Cr3 as readable) but I had no full CR3 sample here. Limits: the pipeline is 8-bit, so RAW highlight headroom beyond LibRaw's default rendering is lost (a 16-bit / float pipeline would be a later step); LibRaw's default rendering is flatter than Lightroom's camera profile. Next: crop tool.

- 2026-09-29: Crop done. `Core/Editing/Crop` is stored like Lightroom: unrotated crop edges (normalised Left/Top/Right/Bottom of the upright image) + straighten `Angle` (±45°, rotation about the crop centre); part of `EditState` and the sidecar (`crop`). `CropGeometry` (tested): handle drags in the frame's rotated axes (corner keeps the opposite corner, edges, move slides along the border), aspect lock, `WithAngle` shrinks to the largest same-ratio frame that fits, results are always inside the image (binary search towards the proposed frame). Rendering: masks and adjustments run on the full image; the vignette is now post-crop (shader uniforms `vignetteCenter/Half/Rotation`, parity-tested with a rotated crop); export cuts the rotated crop with bilinear, edge-clamped sampling (`CpuAdjustmentRenderer.ApplyCrop`). Viewer: shows the cropped result (display space = crop frame; view↔image mapping goes through it, so brush/gradients work on a straightened crop); "Crop (C)" shows the whole image with dimmed outside, thirds grid and 8 handles; Enter/Esc leaves. Panel: aspect presets (Free, Original, 1:1, 3:2, 4:3, 5:4, 7:5, 16:9; portrait crops use the inverse), swap orientation (X), Straighten slider, Reset crop, output size. Verified under Xvfb with the CR2 (drag, straighten, reload from sidecar, radial gradient on a rotated crop). Straightening keeps the crop from when straightening started so turning back restores its size; there is no "grow back to max" and no drag-outside-to-rotate. Next: Lightroom XMP.

- 2026-09-29: Lightroom XMP done. `Core/Editing/LightroomXmp` writes Adobe Camera Raw settings (`crs:`, ProcessVersion 11.0) to `IMG_0001.xmp` next to proprietary RAW files (Lightroom only reads sidecars for those; JPEG/TIFF/PNG/DNG need XMP embedded in the file, not done). Mapped slider-for-slider: Exposure2012/Contrast2012/Highlights2012/Shadows2012/Whites2012/Blacks2012, Vibrance, Saturation, HSL (Hue/Saturation/LuminanceAdjustment<Band>), PostCropVignette*, Crop* + HasCrop, and masks that are a single linear or radial gradient as legacy `GradientBasedCorrections` / `CircularGradientBasedCorrections` (Local*2012 values, local exposure ±1 = ±4 EV). Crop and gradient coordinates are converted to the sensor orientation (`ImageGeometry`; RAW orientation read from TIFF IFD0 or the CR3 CMT1 box). Not written (reported in the status bar): brush masks, multi-component masks, HSL/vibrance/vignette inside masks, and white balance on RAW (Lightroom stores absolute Kelvin). An existing sidecar is merged: only our `crs:` values and the two correction lists are replaced; ratings, keywords, Lightroom's white balance, sharpening etc. are kept. Written together with the JSON on auto-save, only after real edits (`_hasUnsavedEdits`), so opening/closing never rewrites a Lightroom file. On open: JSON first, else settings are imported from the XMP. Round-trip tests for all orientations; verified in the app under Xvfb (write, then import after deleting the JSON). Our rendering differs from Adobe's, so the same numbers look similar, not identical. Next: Phase 4 (AI masks).

- 2026-09-29: XMP for all formats (user request: also JPEG/DNG, never change originals, export just creates a new JPG; no "export for Lightroom"). Every edited photo gets a sidecar; JPEG/PNG write white balance as `IncrementalTemperature/IncrementalTint` (relative, like Lightroom does for rendered files), RAW still skips it. Naming: `IMG_0001.xmp`; when a proprietary RAW shares the base name (RAW+JPEG pairs) the other file uses `IMG_0001.JPG.xmp` so they don't overwrite each other. Orientation for JPEG crops comes from the JPEG's EXIF (`ImageLoader.ReadOrientation`). `ImageExporter` refuses a destination equal to the source (case-insensitive). Note for the user: Lightroom Classic reads sidecars automatically only for proprietary RAW; for JPEG/DNG it reads XMP embedded in the file, so these sidecars may be ignored there. Next: Phase 4 (AI masks) or auto-edit, as the user decides.

- 2026-09-29: Auto done. `Core/Adjustments/AutoAdjust.Suggest` samples a 250×250 grid inside the crop (unedited pixels, so Auto is repeatable) and sets, step by step, each checked through the real pipeline: white balance = half-way grey-world on mid-tones (±40 temp / ±30 tint), exposure = median perceptual brightness → 0.45 with a soft limit (+4 EV needed → ≈ +2, max ±2.5) so night / high-key shots keep their mood, contrast from the 10–90 % spread, highlights from clipping, shadows from the dark fraction, whites / blacks stretch the 0.5 / 99.5 % points towards 0.97 / 0.02 (two passes), vibrance for muted photos. HSL, saturation, vignette and masks are kept. "Auto" toolbar button / Ctrl+U, one undo step, sliders switch to the whole image, status shows the values. Tested on synthetic scenes (dark, bright, flat, colour casts, crop). On the dark blue CR2 it gives +2.4 EV, shadows +50, temp +40 — reasonable but strong; the constants are guesses to tune on real photos. Also fixed sliders showing "-0". Next: Phase 4.

- 2026-09-29: ONNX Runtime integration done (Phase 4 item 1). ONNX Runtime 1.24.4: Core uses the managed API (`Microsoft.ML.OnnxRuntime.Managed`); the app references `Microsoft.ML.OnnxRuntime.DirectML` on Windows (ships DirectML.dll, checked with a win-x64 cross-build) and the CPU package elsewhere; tests use the CPU package. `Core/Ai/OnnxModel` loads a model on DirectML (memory pattern off, sequential, as DML requires) and falls back to CPU with a reason (`Device`, `FallbackReason`); `Run` takes/returns named float tensors. `Core/Ai/ModelStore` keeps models in `%LOCALAPPDATA%\PhotoEditor\models`, downloads on first use to a `.part` file with progress and SHA-256 check (rejects and deletes on mismatch). Tested with a tiny generated model (`tests/.../Assets/sigmoid2x.onnx`, sigmoid(2x)) and a fake HTTP handler. No UI yet: it comes with the first real model. BLOCKER for the next items: huggingface.co is not reachable from this cloud environment, so I cannot download the real models (MobileSAM, BiRefNet, SegFormer) to get their hashes and test inference here. Next: click/box select (MobileSAM) once the network allows Hugging Face.

### To check visually (Windows)
- (When the first AI model exists) status shows DirectML/GPU being used on your PC, not the CPU fallback.
- Auto on a range of your own photos (daylight, backlit, night, snow, portraits): too strong / too weak? Tell me which and I'll tune the constants in `AutoAdjust`.
- Lightroom with a JPEG / DNG edited here: does it pick up `IMG_0001.xmp` (Metadata → Read Metadata from File)? Lightroom Classic is documented to ignore sidecars for these formats.
- Lightroom: import a CR3 edited here (or Metadata → Read Metadata from File): exposure/HSL/vignette/gradients show up; crop matches for a landscape AND a portrait shot; straighten turns the right way (the CropAngle sign is my best guess); linear gradient direction and radial size/feather look right.
- Crop: handles at the image border are half outside the view (still grabbable); drag feel with aspect lock; straighten direction feels natural.
- Open a CR3 from your camera: colours/white balance look right, portrait shots come in upright, exported JPEG has camera + date in Windows Photos.
- Vignette looks natural at defaults on a real photo; roundness extremes.
- Radial gradient: ellipse guides/handles line up with the red overlay at all zoom levels; feather slider feels right.
- Linear gradient: creating by drag, handle hit targets (10 px), guide lines readable on bright and dark photos.
- Brush painting is smooth (no lag) on a real GPU with a 24 MP image; cursor circle size matches the painted stroke; Alt-erase works (Alt might trigger access keys).
- Mask panel layout and ListBox selection; red overlay visible and aligned with the image at all zoom levels (verified under Xvfb only once a component type exists).
- Export… save dialog: JPEG/PNG type choice, suggested name, overwrite prompt; exported JPEG opens in Windows Photos with EXIF (camera, date) intact and correct orientation.
- `\` key toggles before/after on a Windows keyboard layout (bound to OemPipe and OemBackslash).
- HSL sliders on a real photo: sky (Blues luminance −), foliage (Greens/Yellows hue), skin (Oranges) behave as expected, no banding/artefacts at band borders.
- Temperature/tint direction and strength look natural (±100 = exp(±0.35) red/blue gain).
- Light slider strengths feel reasonable on real photos (constants in `PreparedAdjustments.From`).
- Live preview is GPU-accelerated and smooth while dragging sliders on a 24MP JPEG (Xvfb here renders in software).
- Mouse-wheel zoom step feels right with a real wheel / precision touchpad (1.25x per notch).
- File dialog filter and drag & drop of a JPEG from Explorer.
- A portrait phone JPEG (EXIF rotated) shows upright.

- 2026-09-29: Project created with the Avalonia MVVM template; builds cleanly. Next: open image + viewer.
