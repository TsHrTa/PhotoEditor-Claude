using System.Buffers;
using SkiaSharp;

namespace PhotoEditor.Core.Masks;

/// <summary>Turns a <see cref="Mask"/> into a grayscale raster (every mask type ends up as the same kind of image).</summary>
public static class MaskRasterizer
{
    /// <summary>Combined coverage (0..1) of all components, row-major.</summary>
    public static float[] Rasterize(Mask mask, int width, int height)
    {
        var result = new float[width * height];
        RasterizeInto(result, mask, width, height);
        return result;
    }

    /// <summary>Writes the combined coverage into <paramref name="result"/> (length ≥ width × height).</summary>
    public static void RasterizeInto(Span<float> result, Mask mask, int width, int height)
    {
        int n = width * height;
        result = result[..n];
        if (mask.Components.Count == 0)
        {
            result.Clear();
            return;
        }

        // The first component defines the starting mask whatever its mode.
        RenderComponent(mask.Components[0], result, width, height);
        if (mask.Components.Count == 1)
            return;

        var pooled = ArrayPool<float>.Shared.Rent(n);
        try
        {
            var component = pooled.AsSpan(0, n);
            for (int i = 1; i < mask.Components.Count; i++)
            {
                RenderComponent(mask.Components[i], component, width, height);
                Combine(result, component, mask.Components[i].Mode);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooled);
        }
    }

    private static void RenderComponent(MaskComponent c, Span<float> target, int width, int height)
    {
        c.Render(target, width, height);
        if (c.Invert)
        {
            for (int i = 0; i < target.Length; i++)
                target[i] = 1f - target[i];
        }
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
        int n = width * height;
        var pooled = ArrayPool<float>.Shared.Rent(n);
        try
        {
            RasterizeInto(pooled, mask, width, height);
            var bytes = new byte[n];
            for (int i = 0; i < n; i++)
                bytes[i] = (byte)(Math.Clamp(pooled[i], 0f, 1f) * 255f + 0.5f);
            return bytes;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooled);
        }
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
