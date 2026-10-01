using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Unsharp mask on luminance, CPU version of the shader's <c>sharpen</c>: a 5 × 5 Gaussian (sigma = one tap
/// step = the radius) with bilinear, edge-clamped taps like Skia's linear sampling.
/// </summary>
public static class Sharpening
{
    /// <summary>Rows per work item; each strip blurs its rows (plus margins) horizontally into its own buffer.</summary>
    private const int StripRows = 64;

    /// <summary>Returns a sharpened RGBA8888 premultiplied copy of <paramref name="source"/> (same size).</summary>
    /// <remarks>
    /// The 5 × 5 bilinear Gaussian is separable (both the weights and bilinear interpolation factor into x and y),
    /// so it runs as a horizontal then a vertical 5-tap pass with the same result as the shader's 25 taps.
    /// </remarks>
    public static SKBitmap Apply(SKBitmap source, in PreparedAdjustments p, float pixelScale = 1)
    {
        using var converted = source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Premul
            ? null
            : source.Copy(SKColorType.Rgba8888);
        var input = converted ?? source;
        int width = input.Width, height = input.Height, rowBytes = input.RowBytes;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        int outRowBytes = result.RowBytes;
        nint inPtr = input.GetPixels(), outPtr = result.GetPixels();
        float step = p.SharpenRadius * pixelScale, amount = p.SharpenAmount, masking = p.SharpenMasking;

        // Tap k (offset k-2): pixel offset and bilinear fraction are the same for every pixel.
        var offsets = new int[5];
        var fractions = new float[5];
        var weights = new float[5];
        float weightSum = 0;
        for (int k = 0; k < 5; k++)
        {
            float d = (k - 2) * step;
            offsets[k] = (int)MathF.Floor(d);
            fractions[k] = d - offsets[k];
            weightSum += weights[k] = MathF.Exp(-0.5f * (k - 2) * (k - 2));
        }
        for (int k = 0; k < 5; k++)
            weights[k] /= weightSum;
        int margin = (int)MathF.Ceiling(2 * step) + 1;

        int strips = (height + StripRows - 1) / StripRows;
        Parallel.For(0, strips, strip =>
        {
            int y0 = strip * StripRows, y1 = Math.Min(height, y0 + StripRows);
            int rowMin = Math.Max(0, y0 - margin), rowMax = Math.Min(height - 1, y1 - 1 + margin);
            // Horizontally blurred premultiplied RGBA (0..1) of rows rowMin..rowMax.
            var h = new float[(rowMax - rowMin + 1) * width * 4];
            unsafe
            {
                byte* src = (byte*)inPtr;
                for (int r = rowMin; r <= rowMax; r++)
                {
                    byte* row = src + (long)r * rowBytes;
                    int hRow = (r - rowMin) * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        float c0 = 0, c1 = 0, c2 = 0, c3 = 0;
                        for (int k = 0; k < 5; k++)
                        {
                            byte* a = row + Math.Clamp(x + offsets[k], 0, width - 1) * 4;
                            byte* b = row + Math.Clamp(x + offsets[k] + 1, 0, width - 1) * 4;
                            float wb = weights[k] * fractions[k], wa = weights[k] - wb;
                            c0 += wa * a[0] + wb * b[0];
                            c1 += wa * a[1] + wb * b[1];
                            c2 += wa * a[2] + wb * b[2];
                            c3 += wa * a[3] + wb * b[3];
                        }
                        int o = hRow + x * 4;
                        h[o] = c0 / 255f; h[o + 1] = c1 / 255f; h[o + 2] = c2 / 255f; h[o + 3] = c3 / 255f;
                    }
                }

                for (int y = y0; y < y1; y++)
                {
                    byte* px = src + (long)y * rowBytes;
                    byte* dst = (byte*)outPtr + (long)y * outRowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        byte a8 = px[x * 4 + 3];
                        if (a8 == 0)
                        {
                            dst[x * 4] = dst[x * 4 + 1] = dst[x * 4 + 2] = dst[x * 4 + 3] = 0;
                            continue;
                        }
                        float br = 0, bg = 0, bb = 0, ba = 0;
                        for (int k = 0; k < 5; k++)
                        {
                            int ra = Math.Clamp(y + offsets[k], 0, height - 1) - rowMin;
                            int rb = Math.Clamp(y + offsets[k] + 1, 0, height - 1) - rowMin;
                            int ia = (ra * width + x) * 4, ib = (rb * width + x) * 4;
                            float wb = weights[k] * fractions[k], wa = weights[k] - wb;
                            br += wa * h[ia] + wb * h[ib];
                            bg += wa * h[ia + 1] + wb * h[ib + 1];
                            bb += wa * h[ia + 2] + wb * h[ib + 2];
                            ba += wa * h[ia + 3] + wb * h[ib + 3];
                        }

                        float inv = 1f / a8;
                        float sr = px[x * 4] * inv, sg = px[x * 4 + 1] * inv, sb = px[x * 4 + 2] * inv;
                        if (ba > 0)
                        {
                            br /= ba; bg /= ba; bb /= ba;
                        }
                        else
                        {
                            br = sr; bg = sg; bb = sb;
                        }

                        float detail = 0.2126f * (sr - br) + 0.7152f * (sg - bg) + 0.0722f * (sb - bb);
                        float weight = masking > 0 ? ToneCurve.SmoothStep(masking * 0.02f, masking * 0.08f, MathF.Abs(detail)) : 1f;
                        float add = amount * detail * weight;
                        float alpha = a8 / 255f;
                        dst[x * 4] = ColorMath.ToByte(Math.Clamp(sr + add, 0f, 1f) * alpha);
                        dst[x * 4 + 1] = ColorMath.ToByte(Math.Clamp(sg + add, 0f, 1f) * alpha);
                        dst[x * 4 + 2] = ColorMath.ToByte(Math.Clamp(sb + add, 0f, 1f) * alpha);
                        dst[x * 4 + 3] = a8;
                    }
                }
            }
        });
        return result;
    }
}
