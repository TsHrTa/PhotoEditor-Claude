using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>Decodes image files into upright RGBA bitmaps.</summary>
public static class ImageLoader
{
    /// <summary>File extensions the loader can decode: SkiaSharp formats plus camera RAW (via LibRaw).</summary>
    public static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", .. RawImageLoader.Extensions];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Loads an image and applies its EXIF orientation so the result is upright.</summary>
    public static SKBitmap Load(string path)
    {
        if (RawImageLoader.IsRaw(path))
            return RawImageLoader.Load(path);

        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException($"Unsupported or corrupt image: {path}");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Could not decode image ({result}): {path}");
        }

        var upright = ApplyOrientation(bitmap, codec.EncodedOrigin);
        if (!ReferenceEquals(upright, bitmap))
            bitmap.Dispose();
        return upright;
    }

    /// <summary>The file's EXIF orientation (TopLeft when unknown), without decoding the pixels.</summary>
    public static SKEncodedOrigin ReadOrientation(string path)
    {
        if (RawImageLoader.IsRaw(path))
            return RawImageLoader.ReadOrientation(path);
        using var codec = SKCodec.Create(path);
        return codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft;
    }

    /// <summary>Returns a bitmap rotated/flipped according to <paramref name="origin"/> (or the input if already upright).</summary>
    public static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return source;

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? source.Height : source.Width;
        int h = swap ? source.Width : source.Height;

        var result = new SKBitmap(new SKImageInfo(w, h, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(OrientationMatrix(origin, source.Width, source.Height));
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }

    /// <summary>Matrix mapping the stored pixel grid to the upright image.</summary>
    public static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}
