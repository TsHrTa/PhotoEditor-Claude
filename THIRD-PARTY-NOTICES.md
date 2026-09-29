# Third-party notices

PhotoEditor is for personal use. AI models are downloaded on first use into
`%LOCALAPPDATA%\PhotoEditor\models`; each file is pinned to a repository revision and checked by SHA-256
(see `src/PhotoEditor.Core/Ai/ModelCatalog.cs`).

## AI models

| Feature | Model | Source | Licence |
|---|---|---|---|
| Select Object (click / box) | SAM 2.1 Hiera-Tiny (Meta), ONNX conversion | [onnx-community/sam2.1-hiera-tiny-ONNX](https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX) @ `814a0666` | Apache-2.0 (SAM 2.1 weights and code by Meta) |
| Select Subject | BiRefNet-lite (Zheng Peng et al.), ONNX conversion | [onnx-community/BiRefNet_lite-ONNX](https://huggingface.co/onnx-community/BiRefNet_lite-ONNX) @ `de15b22b` | MIT |
| Select Sky / People | SegFormer-B2 fine-tuned on ADE20K (NVIDIA), ONNX conversion | [Xenova/segformer-b2-finetuned-ade-512-512](https://huggingface.co/Xenova/segformer-b2-finetuned-ade-512-512) @ `df795789` | NVIDIA Source Code License for SegFormer: **non-commercial use only** (acceptable because this app is for personal use; replace before any commercial use) |
| AI Denoise | SCUNet real-world PSNR (Kai Zhang et al.), ONNX export | [Heliosoph/scunet-onnx](https://huggingface.co/Heliosoph/scunet-onnx) @ `6d11417e` | Apache-2.0 (repo) / MIT (upstream KAIR code and weights) |
| AI Deblur | NAFNet deblurring (Megvii Research), ONNX by the OpenCV model zoo | [opencv/deblurring_nafnet](https://huggingface.co/opencv/deblurring_nafnet) @ `f1f25511` | MIT |

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
