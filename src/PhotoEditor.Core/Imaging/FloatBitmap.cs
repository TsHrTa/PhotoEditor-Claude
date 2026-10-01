using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Helpers for RgbaF16 bitmaps (the unrounded result of <see cref="Adjustments.CpuAdjustmentRenderer.RenderFloat"/>):
/// the one place where an export rounds to 8 bits.
/// </summary>
public static class FloatBitmap
{
    /// <summary>
    /// Rounds an RgbaF16 bitmap (premultiplied, sRGB-encoded, 0..1) to RGBA8888. With <paramref name="dither"/> a
    /// per-pixel offset of up to ±½ step is added before rounding, so the quantisation error of smooth gradients (skies)
    /// does not line up into visible bands. The offset is a hash of the position: the same photo always gives the same
    /// bytes, and flat black / white stay exact.
    /// </summary>
    public static SKBitmap ToBytes(SKBitmap source, bool dither = true)
    {
        if (source.ColorType != SKColorType.RgbaF16)
            return source.Copy(SKColorType.Rgba8888);
        int width = source.Width, height = source.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        nint src = source.GetPixels(), dst = result.GetPixels();
        int srcRow = source.RowBytes, dstRow = result.RowBytes;
        Parallel.For(0, height, y =>
        {
            unsafe
            {
                var from = new ReadOnlySpan<Half>((byte*)src + (long)y * srcRow, width * 4);
                var to = new Span<byte>((byte*)dst + (long)y * dstRow, width * 4);
                for (int x = 0; x < width; x++)
                {
                    float noise = dither ? Noise(x, y) : 0f;
                    int i = x * 4;
                    for (int c = 0; c < 3; c++)
                        to[i + c] = (byte)Math.Clamp((int)MathF.Floor((float)from[i + c] * 255f + 0.5f + noise), 0, 255);
                    to[i + 3] = (byte)Math.Clamp((int)MathF.Round((float)from[i + 3] * 255f), 0, 255);
                }
            }
        });
        return result;
    }

    /// <summary>A repeatable pseudo-random offset in [−0.5, 0.5) for a pixel (integer hash).</summary>
    private static float Noise(int x, int y)
    {
        uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24)) - 0.5f;
    }
}
