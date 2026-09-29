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
                float v = (logits[y0 * sourceWidth + x0] * (1 - tx) + logits[y0 * sourceWidth + x1] * tx) * (1 - ty)
                        + (logits[y1 * sourceWidth + x0] * (1 - tx) + logits[y1 * sourceWidth + x1] * tx) * ty;
                result[y * width + x] = 1f / (1f + MathF.Exp(-v));
            }
        }
        return result;
    }
}
