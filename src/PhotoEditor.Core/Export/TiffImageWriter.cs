using ImageMagick;
using SkiaSharp;

namespace PhotoEditor.Core.Export;

/// <summary>
/// Writes a 16-bit RGB TIFF (LZW, sRGB profile) from a rendered bitmap; an RgbaF16 bitmap is not rounded to 8 bits
/// first. No EXIF: ImageMagick's TIFF writer drops the exif profile (checked), so the camera data is not carried over.
/// </summary>
public static class TiffImageWriter
{
    /// <param name="bitmap">RgbaF16 (the float render) or RGBA8888 (premultiplied; transparency is dropped).</param>
    public static byte[] Encode(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var samples = new ushort[(long)width * height * 3];
        nint src = bitmap.GetPixels();
        int rowBytes = bitmap.RowBytes;
        bool half = bitmap.ColorType == SKColorType.RgbaF16;
        if (!half && bitmap.ColorType != SKColorType.Rgba8888)
            throw new NotSupportedException($"Unsupported colour type {bitmap.ColorType}");
        Parallel.For(0, height, y =>
        {
            unsafe
            {
                int o = y * width * 3;
                if (half)
                {
                    var row = new ReadOnlySpan<Half>((byte*)src + (long)y * rowBytes, width * 4);
                    for (int x = 0; x < width; x++)
                        for (int c = 0; c < 3; c++)
                            samples[o + x * 3 + c] = (ushort)Math.Clamp((int)MathF.Round((float)row[x * 4 + c] * 65535f), 0, 65535);
                }
                else
                {
                    var row = new ReadOnlySpan<byte>((byte*)src + (long)y * rowBytes, width * 4);
                    for (int x = 0; x < width; x++)
                    {
                        float a = row[x * 4 + 3] / 255f;
                        for (int c = 0; c < 3; c++)
                            samples[o + x * 3 + c] = (ushort)Math.Clamp((int)MathF.Round(a > 0 ? row[x * 4 + c] / 255f / a * 65535f : 0f), 0, 65535);
                    }
                }
            }
        });

        var data = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, data, 0, data.Length);
        using var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)width, (uint)height, StorageType.Short, PixelMapping.RGB));
        image.Depth = 16;
        image.SetProfile(ColorProfile.SRGB);
        image.Settings.Compression = CompressionMethod.LZW;
        return image.ToByteArray(MagickFormat.Tiff);
    }
}
