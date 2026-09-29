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
    public static Tensor Preprocess(SKBitmap image) => ImageTensor.Normalized(image, InputSize, InputSize);

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
    public static float[] UpscaleLogits(float[] logits, int size, int width, int height) =>
        ImageTensor.LogitsToCoverage(logits, size, size, width, height);

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
