using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>The encoder's output for one photo; reused for every click on that photo.</summary>
public sealed record SamEmbedding(Tensor HighRes0, Tensor HighRes1, Tensor Image);

/// <summary>
/// Click / box selection with SAM 2.1 (tiny): the encoder analyses the photo once (1–3 s on a CPU, much
/// less on a GPU), then each set of clicks is turned into a mask in a fraction of a second.
/// </summary>
/// <remarks>
/// SAM 2 takes the photo squashed to 1024 × 1024 (no padding), normalised with the ImageNet mean / std;
/// prompts are in that 1024 × 1024 space. It returns three candidate masks as 256 × 256 logits with a
/// predicted quality score each; the best one is upscaled, smoothed with a sigmoid and stored as a PNG.
/// </remarks>
public sealed class SegmentAnything : IDisposable
{
    public const int InputSize = 1024;

    /// <summary>Long side of the stored mask raster.</summary>
    public const int MaskSize = 1024;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly OnnxModel _encoder;
    private readonly OnnxModel _decoder;

    private SegmentAnything(OnnxModel encoder, OnnxModel decoder)
    {
        _encoder = encoder;
        _decoder = decoder;
    }

    public InferenceDevice Device => _encoder.Device;

    public string? FallbackReason => _encoder.FallbackReason;

    public static SegmentAnything Load(string encoderPath, string decoderPath, InferenceDevice preferred = InferenceDevice.DirectML)
    {
        var encoder = OnnxModel.Load(encoderPath, preferred);
        try
        {
            return new SegmentAnything(encoder, OnnxModel.Load(decoderPath, preferred));
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>Loads the models from a <see cref="ModelStore"/> (they must be downloaded already).</summary>
    public static SegmentAnything Load(ModelStore store, InferenceDevice preferred = InferenceDevice.DirectML) =>
        Load(store.PathOf(ModelCatalog.SamEncoder), store.PathOf(ModelCatalog.SamDecoder), preferred);

    /// <summary>The normalised 1 × 3 × 1024 × 1024 input for <paramref name="image"/>.</summary>
    public static Tensor Preprocess(SKBitmap image)
    {
        using var resized = image.Resize(new SKImageInfo(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("Could not resize the photo for the AI model.");
        var pixels = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes, plane = InputSize * InputSize;
        var data = new float[3 * plane];
        for (int y = 0; y < InputSize; y++)
        for (int x = 0; x < InputSize; x++)
        {
            int o = y * rowBytes + x * 4, i = y * InputSize + x;
            for (int c = 0; c < 3; c++)
                data[c * plane + i] = (pixels[o + c] / 255f - Mean[c]) / Std[c];
        }
        return new Tensor(data, [1, 3, InputSize, InputSize]);
    }

    /// <summary>Analyses the photo (the slow part); keep the result for all clicks on it.</summary>
    public SamEmbedding Encode(SKBitmap image)
    {
        var outputs = _encoder.Run(new Dictionary<string, Tensor> { ["pixel_values"] = Preprocess(image) });
        return new SamEmbedding(outputs["image_embeddings.0"], outputs["image_embeddings.1"], outputs["image_embeddings.2"]);
    }

    /// <summary>
    /// The best of the three candidate masks for the prompts, as 256 × 256 logits (&gt; 0 = inside), and its score.
    /// </summary>
    public (float[] Logits, int Size, float Score) PredictLogits(SamEmbedding embedding, IReadOnlyList<SelectPoint> points, SelectBox? box)
    {
        if (points.Count == 0 && box is null)
            throw new ArgumentException("At least one click or a box is needed.");
        var coords = new float[points.Count * 2];
        var labels = new long[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            coords[i * 2] = points[i].X * InputSize;
            coords[i * 2 + 1] = points[i].Y * InputSize;
            labels[i] = points[i].Include ? 1 : 0;
        }
        float[] boxes = box is { } b ? [b.Left * InputSize, b.Top * InputSize, b.Right * InputSize, b.Bottom * InputSize] : [];

        var outputs = _decoder.Run(new Dictionary<string, Tensor>
        {
            ["input_points"] = new Tensor(coords, [1, 1, points.Count, 2]),
            ["input_labels"] = Tensor.Int64(labels, [1, 1, points.Count]),
            ["input_boxes"] = new Tensor(boxes, [1, box is null ? 0 : 1, 4]),
            ["image_embeddings.0"] = embedding.HighRes0,
            ["image_embeddings.1"] = embedding.HighRes1,
            ["image_embeddings.2"] = embedding.Image,
        });
        var scores = outputs["iou_scores"].Data;
        var masks = outputs["pred_masks"];
        int count = (int)masks.Shape[2], h = (int)masks.Shape[3], w = (int)masks.Shape[4];
        int best = 0;
        for (int i = 1; i < Math.Min(count, scores.Length); i++)
        {
            if (scores[i] > scores[best])
                best = i;
        }
        return (masks.Data.AsSpan(best * h * w, h * w).ToArray(), w, scores[best]);
    }

    /// <summary>
    /// Runs the prompts and returns the mask component (raster with the image's aspect ratio, long side
    /// <see cref="MaskSize"/>, soft edges from the upscaled logits).
    /// </summary>
    public RasterMaskComponent Select(SamEmbedding embedding, IReadOnlyList<SelectPoint> points, SelectBox? box, int imageWidth, int imageHeight)
    {
        var (logits, size, _) = PredictLogits(embedding, points, box);
        var (w, h) = Imaging.PreviewImage.PreviewSize(imageWidth, imageHeight, MaskSize);
        var coverage = UpscaleLogits(logits, size, w, h);
        return new RasterMaskComponent
        {
            Source = "object",
            Points = [.. points],
            Box = box,
            MaskPng = RasterMaskComponent.EncodePng(coverage, w, h),
        };
    }

    /// <summary>Bilinearly upsamples square logits to <paramref name="width"/> × <paramref name="height"/> and applies a sigmoid.</summary>
    public static float[] UpscaleLogits(float[] logits, int size, int width, int height)
    {
        var result = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * size / height - 0.5f, 0, size - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, size - 1);
            float ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * size / width - 0.5f, 0, size - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, size - 1);
                float tx = fx - x0;
                float v = (logits[y0 * size + x0] * (1 - tx) + logits[y0 * size + x1] * tx) * (1 - ty)
                        + (logits[y1 * size + x0] * (1 - tx) + logits[y1 * size + x1] * tx) * ty;
                result[y * width + x] = 1f / (1f + MathF.Exp(-v));
            }
        }
        return result;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
