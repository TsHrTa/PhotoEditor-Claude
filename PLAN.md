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
- [ ] Light: exposure, contrast, highlights, shadows, whites, blacks
- [ ] Color: temperature, tint, saturation, vibrance
- [ ] HSL panel: hue / saturation / luminance per color band (reds, oranges, yellows, greens, aquas, blues, purples, magentas)
- [ ] Before/after toggle, reset per slider (double-click) and reset all
- [ ] Export full resolution (JPEG quality, PNG), keep EXIF

### Phase 2 – Edit stack
- [ ] Undo / redo
- [ ] Save/load edit settings as sidecar file (.json next to the image)

### Phase 3 – Masks
- [ ] Mask model: list of masks, each with its own adjustment set; add/subtract/intersect components
- [ ] Mask overlay display (red tint) and mask list panel
- [ ] Brush (size, feather, flow, erase)
- [ ] Linear gradient (on-canvas handles)
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

### To check visually (Windows)
- Live preview is GPU-accelerated and smooth while dragging sliders on a 24MP JPEG (Xvfb here renders in software).
- Mouse-wheel zoom step feels right with a real wheel / precision touchpad (1.25x per notch).
- File dialog filter and drag & drop of a JPEG from Explorer.
- A portrait phone JPEG (EXIF rotated) shows upright.

- 2026-09-29: Project created with the Avalonia MVVM template; builds cleanly. Next: open image + viewer.
