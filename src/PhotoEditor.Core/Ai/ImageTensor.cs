using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>Turns photos into model inputs and model outputs back into mask rasters.</summary>
public static class ImageTensor
{
    public static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    public static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// The photo squashed to <paramref name="width"/> × <paramref name="height"/> (no padding) as a 1 × 3 × H × W
    /// tensor, scaled to 0..1 and normalised with the ImageNet mean / std (what SAM 2 and BiRefNet expect).
    /// </summary>
    public static Tensor Normalized(SKBitmap image, int width, int height)
    {
        using var resized = image.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("Could not resize the photo for the AI model.");
        var pixels = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes, plane = width * height;
        var data = new float[3 * plane];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int o = y * rowBytes + x * 4, i = y * width + x;
            for (int c = 0; c < 3; c++)
                data[c * plane + i] = (pixels[o + c] / 255f - ImageNetMean[c]) / ImageNetStd[c];
        }
        return new Tensor(data, [1, 3, height, width]);
    }

    /// <summary>
    /// Bilinearly resamples a <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/> logit map to
    /// <paramref name="width"/> × <paramref name="height"/> and applies a sigmoid (0..1 coverage with soft edges).
    /// </summary>
    public static float[] LogitsToCoverage(ReadOnlySpan<float> logits, int sourceWidth, int sourceHeight, int width, int height)
    {
        var result = Resample(logits, sourceWidth, sourceHeight, width, height);
        for (int i = 0; i < result.Length; i++)
            result[i] = 1f / (1f + MathF.Exp(-result[i]));
        return result;
    }

    /// <summary>
    /// Per-pixel probability of class <paramref name="classIndex"/> from 1 × C × H × W logits (softmax over the
    /// classes), as an H × W map.
    /// </summary>
    public static float[] ClassProbability(Tensor logits, int classIndex)
    {
        int classes = (int)logits.Shape[1], h = (int)logits.Shape[2], w = (int)logits.Shape[3], plane = w * h;
        var data = logits.Data;
        var result = new float[plane];
        for (int i = 0; i < plane; i++)
        {
            float max = float.MinValue;
            for (int c = 0; c < classes; c++)
                max = MathF.Max(max, data[c * plane + i]);
            float sum = 0;
            for (int c = 0; c < classes; c++)
                sum += MathF.Exp(data[c * plane + i] - max);
            result[i] = MathF.Exp(data[classIndex * plane + i] - max) / sum;
        }
        return result;
    }

    /// <summary>Bilinear resampling of a value map (pixel centres aligned, edges clamped).</summary>
    public static float[] Resample(ReadOnlySpan<float> values, int sourceWidth, int sourceHeight, int width, int height)
    {
        var v0 = values;
        var result = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * sourceHeight / height - 0.5f, 0, sourceHeight - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sourceHeight - 1);
            float ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * sourceWidth / width - 0.5f, 0, sourceWidth - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sourceWidth - 1);
                float tx = fx - x0;
                result[y * width + x] = (v0[y0 * sourceWidth + x0] * (1 - tx) + v0[y0 * sourceWidth + x1] * tx) * (1 - ty)
                                      + (v0[y1 * sourceWidth + x0] * (1 - tx) + v0[y1 * sourceWidth + x1] * tx) * ty;
            }
        }
        return result;
    }
}
