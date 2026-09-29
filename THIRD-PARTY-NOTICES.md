# Third-party notices

PhotoEditor is for personal use. AI models are downloaded on first use into
`%LOCALAPPDATA%\PhotoEditor\models`; each file is pinned to a repository revision and checked by SHA-256
(see `src/PhotoEditor.Core/Ai/ModelCatalog.cs`).

## AI models

| Feature | Model | Source | Licence |
|---|---|---|---|
| Select Object (click / box) | SAM 2.1 Hiera-Tiny (Meta), ONNX conversion | [onnx-community/sam2.1-hiera-tiny-ONNX](https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX) @ `814a0666` | Apache-2.0 (SAM 2.1 weights and code by Meta) |

## Libraries

| Library | Licence |
|---|---|
| Avalonia | MIT |
| CommunityToolkit.Mvvm | MIT |
| SkiaSharp | MIT |
| Magick.NET | Apache-2.0 |
| ImageMagick (inside Magick.NET) | ImageMagick License |
| LibRaw (inside Magick.NET) | LGPL-2.1 or CDDL-1.0 |
| ONNX Runtime (incl. DirectML) | MIT |
