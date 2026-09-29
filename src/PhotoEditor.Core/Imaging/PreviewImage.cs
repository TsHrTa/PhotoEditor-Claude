using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// An opened image for display: the full-resolution original plus a downscaled copy
/// used for fast live preview. Both are immutable and safe to draw from the render thread.
/// </summary>
public sealed class PreviewImage
{
    public const int DefaultMaxPreviewSize = 2560;

    private PreviewImage(SKImage full, SKImage preview)
    {
        Full = full;
        Preview = preview;
    }

    public SKImage Full { get; }
    public SKImage Preview { get; }

    public int Width => Full.Width;
    public int Height => Full.Height;

    /// <summary>Preview pixels per full-resolution pixel (≤ 1).</summary>
    public double PreviewScale => (double)Preview.Width / Full.Width;

    /// <summary>Wraps <paramref name="original"/> (made immutable, pixels shared) and builds the preview.</summary>
    public static PreviewImage Create(SKBitmap original, int maxPreviewSize = DefaultMaxPreviewSize)
    {
        original.SetImmutable();
        var full = SKImage.FromBitmap(original);
        var (w, h) = PreviewSize(original.Width, original.Height, maxPreviewSize);
        if (w == original.Width && h == original.Height)
            return new PreviewImage(full, full);

        using var small = original.Resize(
            new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("Could not create preview image.");
        small.SetImmutable();
        return new PreviewImage(full, SKImage.FromBitmap(small));
    }

    /// <summary>Size that fits within <paramref name="maxSize"/> on the long side, keeping aspect ratio.</summary>
    public static (int Width, int Height) PreviewSize(int width, int height, int maxSize)
    {
        int longSide = Math.Max(width, height);
        if (longSide <= maxSize)
            return (width, height);
        double s = (double)maxSize / longSide;
        return (Math.Max(1, (int)Math.Round(width * s)), Math.Max(1, (int)Math.Round(height * s)));
    }
}
