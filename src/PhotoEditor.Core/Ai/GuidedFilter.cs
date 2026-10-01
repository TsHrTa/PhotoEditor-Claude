using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>
/// Edge-aware smoothing (He et al., "Guided Image Filtering"): makes a coarse mask follow the edges of the
/// photo. Used for masks from low-resolution models (SegFormer outputs 128 × 128), which otherwise leave a
/// halo along sharp boundaries such as roofs against the sky.
/// </summary>
public static class GuidedFilter
{
    /// <summary>
    /// Refines <paramref name="mask"/> (0..1, <paramref name="width"/> × <paramref name="height"/>) with the grayscale
    /// <paramref name="guide"/> of the same size. <paramref name="radius"/> is the window radius in pixels,
    /// <paramref name="epsilon"/> the regularisation (smaller = follows edges more closely).
    /// </summary>
    public static float[] Apply(float[] mask, float[] guide, int width, int height, int radius = 8, float epsilon = 1e-3f)
    {
        int n = width * height;
        var guideMean = BoxMean(guide, width, height, radius);
        var maskMean = BoxMean(mask, width, height, radius);
        var product = new float[n];
        var square = new float[n];
        for (int i = 0; i < n; i++)
        {
            product[i] = guide[i] * mask[i];
            square[i] = guide[i] * guide[i];
        }
        var productMean = BoxMean(product, width, height, radius);
        var squareMean = BoxMean(square, width, height, radius);

        var a = new float[n];
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float covariance = productMean[i] - guideMean[i] * maskMean[i];
            float variance = squareMean[i] - guideMean[i] * guideMean[i];
            a[i] = covariance / (variance + epsilon);
            b[i] = maskMean[i] - a[i] * guideMean[i];
        }
        var aMean = BoxMean(a, width, height, radius);
        var bMean = BoxMean(b, width, height, radius);
        var result = new float[n];
        for (int i = 0; i < n; i++)
            result[i] = Math.Clamp(aMean[i] * guide[i] + bMean[i], 0f, 1f);
        return result;
    }

    /// <summary>Luminance (0..1, sRGB-encoded) of <paramref name="image"/> resized to <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static float[] Guide(SKBitmap image, int width, int height)
    {
        using var resized = image.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("Could not resize the photo.");
        var pixels = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes;
        var guide = new float[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int o = y * rowBytes + x * 4;
            guide[y * width + x] = (0.2126f * pixels[o] + 0.7152f * pixels[o + 1] + 0.0722f * pixels[o + 2]) / 255f;
        }
        return guide;
    }

    /// <summary>Mean over a (2r+1)² window (clipped at the borders), via a summed-area table.</summary>
    public static float[] BoxMean(float[] values, int width, int height, int radius)
    {
        var sat = new double[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            double row = 0;
            for (int x = 0; x < width; x++)
            {
                row += values[y * width + x];
                sat[(y + 1) * (width + 1) + x + 1] = sat[y * (width + 1) + x + 1] + row;
            }
        }
        var result = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(height, y + radius + 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(width, x + radius + 1);
                double sum = sat[y1 * (width + 1) + x1] - sat[y0 * (width + 1) + x1] - sat[y1 * (width + 1) + x0] + sat[y0 * (width + 1) + x0];
                result[y * width + x] = (float)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }
        return result;
    }
}
