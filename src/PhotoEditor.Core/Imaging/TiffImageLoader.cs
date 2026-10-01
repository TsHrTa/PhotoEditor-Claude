using ImageMagick;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// TIFF (8 or 16 bit) through Magick.NET, which Skia can't read. A 16-bit file keeps its extra precision the same way a
/// RAW does: the 8-bit photo plus the <see cref="Headroom.Fine"/> layer (the fraction of an 8-bit step), so lifting
/// shadows or a strong curve does not band. A colour profile that is not sRGB is converted to sRGB; Gray / CMYK are
/// converted to RGB. Alpha is ignored. Upright (the file's orientation applied).
/// </summary>
public static class TiffImageLoader
{
    public static readonly string[] Extensions = [".tif", ".tiff"];

    public static bool IsTiff(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Size and EXIF orientation without reading the pixels.</summary>
    public static (int Width, int Height, SKEncodedOrigin Origin) ReadGeometry(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            var origin = image.Orientation is > OrientationType.Undefined and <= OrientationType.LeftBottom
                ? (SKEncodedOrigin)(int)image.Orientation
                : SKEncodedOrigin.TopLeft;
            return ((int)image.Width, (int)image.Height, origin);
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"Unsupported or corrupt TIFF ({ex.Message})", ex);
        }
    }

    public static SKBitmap Load(string path)
    {
        ushort[] rgb;
        int width, height;
        bool deep;
        try
        {
            using var image = new MagickImage(path);
            deep = image.Depth > 8;
            if (image.GetColorProfile() is { } profile && !IsSrgb(profile))
                image.TransformColorSpace(ColorProfile.SRGB);
            else if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Gray or ColorSpace.Lab or ColorSpace.YCbCr)
                image.TransformColorSpace(ColorProfile.SRGB, ColorTransformMode.Quantum);
            image.AutoOrient();
            width = (int)image.Width;
            height = (int)image.Height;
            rgb = image.GetPixelsUnsafe().ToShortArray("RGB")
                ?? throw new InvalidDataException($"Could not read TIFF pixels: {path}");
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"Unsupported or corrupt TIFF ({ex.Message})", ex);
        }
        return Develop(rgb, width, height, deep);
    }

    private static bool IsSrgb(IColorProfile profile) =>
        profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>16-bit RGB to the 8-bit photo plus (for a deep file) the fraction of a step as a fine layer.</summary>
    public static SKBitmap Develop(ushort[] rgb, int width, int height, bool deep)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var extra = deep ? new SKBitmap(info) : null;
        var fine = deep ? new SKBitmap(info) : null;
        nint photoPtr = bitmap.GetPixels(), extraPtr = extra?.GetPixels() ?? 0, finePtr = fine?.GetPixels() ?? 0;
        int photoRow = bitmap.RowBytes, extraRow = extra?.RowBytes ?? 0, fineRow = fine?.RowBytes ?? 0;
        Parallel.For(0, height, y =>
        {
            unsafe
            {
                byte* p = (byte*)photoPtr + (long)y * photoRow;
                byte* e = deep ? (byte*)extraPtr + (long)y * extraRow : null;
                byte* f = deep ? (byte*)finePtr + (long)y * fineRow : null;
                for (int x = 0, i = y * width * 3; x < width; x++, i += 3)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        ushort v = rgb[i + c];
                        byte b = (byte)((v * 255 + 32767) / 65535);
                        p[x * 4 + c] = b;
                        if (deep)
                        {
                            e![x * 4 + c] = 0;
                            float fraction = v / 65535f - b / 255f;
                            f![x * 4 + c] = (byte)Math.Clamp(MathF.Round(Headroom.FineZero + fraction / Headroom.FineStep), 0f, 255f);
                        }
                    }
                    p[x * 4 + 3] = 255;
                    if (deep)
                        e![x * 4 + 3] = f![x * 4 + 3] = 255;
                }
            }
        });
        if (deep)
            Headroom.Attach(bitmap, new Headroom(extra!, 1e-3f, fine));
        return bitmap;
    }
}
