# PhotoEditor – Plan

A simple Lightroom-style photo editor: light and color adjustments, masks (brush, gradients, AI selection), AI denoise/sharpen.

## Stack
- .NET 10, Avalonia 12, CommunityToolkit.Mvvm
- SkiaSharp 3.119 (display, JPEG/PNG/WebP, GPU runtime shaders for live preview); same version Avalonia.Skia uses
- Magick.NET (TIFF, HEIC, RAW) – later
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
- [ ] Radial gradient (ellipse, feather, invert)
- [ ] Vignette (amount, midpoint, roundness, feather)

### Phase 4 – AI masks
- [ ] ONNX Runtime integration (DirectML with CPU fallback), model download on first use
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

### To check visually (Windows)
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
