using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Colour profiles of photos: a JPEG / PNG / WebP that carries an Adobe RGB, Display P3, ProPhoto or other profile is
/// converted to sRGB (the pipeline's colour space) when it is decoded, instead of being read as if its numbers were
/// sRGB (Adobe RGB read as sRGB is visibly dull, P3 too). Colours outside sRGB are moved into it by keeping their hue
/// and brightness and giving up saturation (<see cref="CompressToGamut"/>), not by clipping each channel.
/// </summary>
public static class IccColor
{
    /// <summary>True when pixels tagged with <paramref name="colorSpace"/> are not sRGB and need converting.</summary>
    public static bool NeedsConversion(SKColorSpace? colorSpace) => colorSpace is not null && !colorSpace.IsSrgb;

    /// <summary>
    /// Decodes <paramref name="codec"/> at <paramref name="size"/> into premultiplied RGBA8888 sRGB: the codec converts to
    /// linear sRGB in float (nothing is clipped there), then the colours are brought into the gamut and encoded.
    /// Null when decoding fails.
    /// </summary>
    public static SKBitmap? DecodeToSrgb(SKCodec codec, SKSizeI size)
    {
        using var linear = SKColorSpace.CreateSrgbLinear();
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.RgbaF16, SKAlphaType.Unpremul, linear);
        using var wide = new SKBitmap(info);
        var result = codec.GetPixels(info, wide.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        return LinearToSrgb8(wide);
    }

    /// <summary>Linear sRGB floats (unpremultiplied RgbaF16, may be outside 0..1) to premultiplied sRGB RGBA8888.</summary>
    public static SKBitmap LinearToSrgb8(SKBitmap linearF16)
    {
        int width = linearF16.Width, height = linearF16.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        nint src = linearF16.GetPixels(), dst = result.GetPixels();
        int srcRow = linearF16.RowBytes, dstRow = result.RowBytes;
        Parallel.For(0, height, y =>
        {
            unsafe
            {
                var from = new ReadOnlySpan<Half>((byte*)src + (long)y * srcRow, width * 4);
                var to = new Span<byte>((byte*)dst + (long)y * dstRow, width * 4);
                for (int i = 0; i < width * 4; i += 4)
                {
                    float r = (float)from[i], g = (float)from[i + 1], b = (float)from[i + 2], a = Math.Clamp((float)from[i + 3], 0f, 1f);
                    CompressToGamut(ref r, ref g, ref b);
                    to[i] = ColorMath.ToByte(ColorMath.LinearToSrgbFast(r) * a);
                    to[i + 1] = ColorMath.ToByte(ColorMath.LinearToSrgbFast(g) * a);
                    to[i + 2] = ColorMath.ToByte(ColorMath.LinearToSrgbFast(b) * a);
                    to[i + 3] = ColorMath.ToByte(a);
                }
            }
        });
        return result;
    }

    /// <summary>
    /// Brings a linear colour into 0..1: a negative channel (a colour more saturated than sRGB can show) is fixed by
    /// reducing the chroma at the same lightness and hue (<see cref="GamutMapping"/>); a channel above 1 scales the
    /// colour down. Colours already inside are unchanged.
    /// </summary>
    public static void CompressToGamut(ref float r, ref float g, ref float b)
    {
        if (MathF.Min(r, MathF.Min(g, b)) < 0f)
        {
            var (l, a, bb) = HslMath.ToOklab(r, g, b);
            (r, g, b) = GamutMapping.FromOklab(l, a, bb);
        }
        float max = MathF.Max(r, MathF.Max(g, b));
        if (max > 1f)
        {
            r /= max;
            g /= max;
            b /= max;
        }
    }
}
