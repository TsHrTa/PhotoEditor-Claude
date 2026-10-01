using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Downscaling of a float render (RgbaF16, sRGB-encoded, opaque, values may exceed 1) in linear light with a Lanczos-3,
/// the export's resize: like <see cref="LinearResampler"/> but without the 8-bit layers, and the result stays float.
/// </summary>
public static class FloatResampler
{
    /// <summary>Null when the input is not an opaque RgbaF16 bitmap or the target is not a reduction.</summary>
    public static SKBitmap? TryResize(SKBitmap source, int width, int height)
    {
        if (source.ColorType != SKColorType.RgbaF16 || width > source.Width || height > source.Height || width < 1 || height < 1)
            return null;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul));
        var horizontal = LinearResampler.Kernel.Build(source.Width, width);
        var vertical = LinearResampler.Kernel.Build(source.Height, height);
        const int band = 32;
        int bands = (height + band - 1) / band, sourceWidth = source.Width;
        nint src = source.GetPixels(), dst = result.GetPixels();
        int srcRow = source.RowBytes, dstRow = result.RowBytes;
        bool opaque = true;
        Parallel.For(0, bands, b =>
        {
            int y0 = b * band, y1 = Math.Min(height, y0 + band);
            int s0 = int.MaxValue, s1 = 0;
            for (int y = y0; y < y1; y++)
            {
                s0 = Math.Min(s0, vertical.Start[y]);
                s1 = Math.Max(s1, vertical.Start[y] + vertical.Count[y]);
            }
            var rows = new float[(s1 - s0) * width * 3];
            var decoded = new float[sourceWidth * 3];
            unsafe
            {
                for (int sy = s0; sy < s1; sy++)
                {
                    var from = new ReadOnlySpan<Half>((byte*)src + (long)sy * srcRow, sourceWidth * 4);
                    for (int x = 0; x < sourceWidth; x++)
                    {
                        if ((float)from[x * 4 + 3] < 0.999f)
                            opaque = false;
                        decoded[x * 3] = ColorMath.SrgbToLinearFast((float)from[x * 4]);
                        decoded[x * 3 + 1] = ColorMath.SrgbToLinearFast((float)from[x * 4 + 1]);
                        decoded[x * 3 + 2] = ColorMath.SrgbToLinearFast((float)from[x * 4 + 2]);
                    }
                    int rowBase = (sy - s0) * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        int start = horizontal.Start[x], count = horizontal.Count[x], k = x * horizontal.Stride;
                        float r = 0, g = 0, bl = 0;
                        for (int i = 0; i < count; i++)
                        {
                            float w = horizontal.Weights[k + i];
                            int at = (start + i) * 3;
                            r += w * decoded[at];
                            g += w * decoded[at + 1];
                            bl += w * decoded[at + 2];
                        }
                        rows[rowBase + x * 3] = r;
                        rows[rowBase + x * 3 + 1] = g;
                        rows[rowBase + x * 3 + 2] = bl;
                    }
                }
                for (int y = y0; y < y1; y++)
                {
                    var to = new Span<Half>((byte*)dst + (long)y * dstRow, width * 4);
                    int start = vertical.Start[y], count = vertical.Count[y], k = y * vertical.Stride;
                    for (int x = 0; x < width; x++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float v = 0;
                            for (int i = 0; i < count; i++)
                                v += vertical.Weights[k + i] * rows[((start + i - s0) * width + x) * 3 + c];
                            to[x * 4 + c] = (Half)ColorMath.LinearToSrgbFast(MathF.Max(v, 0f));
                        }
                        to[x * 4 + 3] = (Half)1f;
                    }
                }
            }
        });
        if (opaque)
            return result;
        result.Dispose();
        return null;
    }
}
