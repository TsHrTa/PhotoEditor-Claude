using SkiaSharp;

namespace PhotoEditor.Core.Masks;

/// <summary>Turns a <see cref="Mask"/> into a grayscale raster (every mask type ends up as the same kind of image).</summary>
public static class MaskRasterizer
{
    /// <summary>Combined coverage (0..1) of all components, row-major.</summary>
    public static float[] Rasterize(Mask mask, int width, int height)
    {
        var result = new float[width * height];
        var component = new float[width * height];
        bool first = true;
        foreach (var c in mask.Components)
        {
            c.Render(component, width, height);
            if (c.Invert)
            {
                for (int i = 0; i < component.Length; i++)
                    component[i] = 1f - component[i];
            }
            // The first component defines the starting mask whatever its mode.
            Combine(result, component, first ? MaskMode.Add : c.Mode);
            first = false;
        }
        return result;
    }

    public static void Combine(Span<float> mask, ReadOnlySpan<float> component, MaskMode mode)
    {
        for (int i = 0; i < mask.Length; i++)
        {
            float c = Math.Clamp(component[i], 0f, 1f);
            mask[i] = mode switch
            {
                MaskMode.Add => MathF.Max(mask[i], c),
                MaskMode.Subtract => mask[i] * (1f - c),
                _ => mask[i] * c,
            };
        }
    }

    /// <summary>The mask quantised to 8 bits (0..255), row-major without padding. Used by the CPU renderer.</summary>
    public static byte[] RasterizeToBytes(Mask mask, int width, int height)
    {
        var coverage = Rasterize(mask, width, height);
        var bytes = new byte[coverage.Length];
        for (int i = 0; i < coverage.Length; i++)
            bytes[i] = (byte)MathF.Round(Math.Clamp(coverage[i], 0f, 1f) * 255f);
        return bytes;
    }

    /// <summary>The mask as an 8-bit grayscale bitmap (shader input), identical to <see cref="RasterizeToBytes"/>.</summary>
    public static SKBitmap RasterizeToBitmap(Mask mask, int width, int height)
    {
        var bytes = RasterizeToBytes(mask, width, height);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;
        for (int y = 0; y < height; y++)
            bytes.AsSpan(y * width, width).CopyTo(pixels.Slice(y * rowBytes, width));
        return bitmap;
    }
}
