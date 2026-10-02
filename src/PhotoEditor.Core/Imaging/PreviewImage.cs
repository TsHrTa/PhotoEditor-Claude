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

    // What the smaller levels are made from (null: this image has no pyramid, see ImageFor).
    private SKBitmap? _previewBitmap;
    private Headroom? _previewHeadroom;
    private Lens.LensShading? _vignetting;
    private readonly Dictionary<int, SKImage> _levels = [];
    private readonly object _levelLock = new();

    /// <summary>Smallest width of a pyramid level.</summary>
    private const int MinLevelWidth = 360;

    /// <summary>
    /// The image to draw when the view shows the photo at <paramref name="viewScale"/> (screen pixels per full-resolution
    /// pixel): the full image when zoomed in past the preview, otherwise the smallest pyramid level that is not smaller
    /// than the screen size (levels step by 1/sqrt 2 from the preview, each made with the Lanczos in linear light). Drawn
    /// by the GPU at 1 to 1.4 times the screen size it needs no mipmaps, which blur fine detail (leaves, hair); drawing the
    /// 2560 px preview into a 1000 px window through trilinear mipmaps did.
    /// </summary>
    public SKImage ImageFor(double viewScale)
    {
        if (viewScale > PreviewScale * 1.01)
            return Full;
        if (_previewBitmap is null)
            return Preview;
        int level = 0;
        while (level < 6 && Preview.Width * Math.Pow(0.5, (level + 1) / 2.0) / Full.Width >= viewScale
            && Preview.Width * Math.Pow(0.5, (level + 1) / 2.0) >= MinLevelWidth)
            level++;
        return level == 0 ? Preview : Level(level);
    }

    private SKImage Level(int level)
    {
        lock (_levelLock)
        {
            if (_levels.TryGetValue(level, out var cached))
                return cached;
            int width = Math.Max(1, (int)Math.Round(Preview.Width * Math.Pow(0.5, level / 2.0)));
            int height = Math.Max(1, (int)Math.Round((double)Preview.Height * width / Preview.Width));
            SKBitmap small;
            Headroom? headroom;
            if (LinearResampler.TryResize(_previewBitmap!, _previewHeadroom, width, height) is { } resized)
                (small, headroom) = resized;
            else
            {
                small = Downscale(_previewBitmap!, width, height);
                headroom = _previewHeadroom?.Resized(width, height);
            }
            small.SetImmutable();
            var image = SKImage.FromBitmap(small);
            Headroom.Attach(image, headroom);
            Lens.LensVignetting.Attach(image, _vignetting);
            _levels[level] = image;
            return image;
        }
    }

    /// <summary>
    /// This image stands in for the photo while it decodes (the half-size quick render of a RAW, or the camera's JPEG):
    /// the viewer keeps what it shows when the full photo replaces it, even at another resolution.
    /// </summary>
    public bool IsStandIn { get; init; }

    public int Width => Full.Width;
    public int Height => Full.Height;

    /// <summary>Preview pixels per full-resolution pixel (≤ 1).</summary>
    public double PreviewScale => (double)Preview.Width / Full.Width;

    /// <summary>Wraps <paramref name="original"/> (made immutable, pixels shared) and builds the preview.</summary>
    public static PreviewImage Create(SKBitmap original, int maxPreviewSize = DefaultMaxPreviewSize, bool standIn = false)
    {
        original.SetImmutable();
        var full = SKImage.FromBitmap(original);
        var headroom = Headroom.Of(original);
        Headroom.Attach(full, headroom);
        var vignetting = Lens.LensVignetting.Of(original);
        Lens.LensVignetting.Attach(full, vignetting);
        var (w, h) = PreviewSize(original.Width, original.Height, maxPreviewSize);
        if (w == original.Width && h == original.Height)
            return new PreviewImage(full, full) { IsStandIn = standIn };

        // Linear-light Lanczos (see LinearResampler); the layers of a RAW are filtered with the photo.
        SKBitmap small;
        Headroom? smallHeadroom;
        if (LinearResampler.TryResize(original, headroom, w, h) is { } resized)
            (small, smallHeadroom) = resized;
        else
        {
            small = Downscale(original, w, h);
            smallHeadroom = headroom?.Resized(w, h);
        }
        small.SetImmutable();
        var preview = SKImage.FromBitmap(small);
        Headroom.Attach(preview, smallHeadroom);
        Lens.LensVignetting.Attach(preview, vignetting);
        return new PreviewImage(full, preview) { IsStandIn = standIn, _previewBitmap = small, _previewHeadroom = smallHeadroom, _vignetting = vignetting };
    }

    /// <summary>A preview image made of existing images (the preview may be the full image itself).</summary>
    public static PreviewImage FromImages(SKImage full, SKImage preview) => new(full, preview);

    /// <summary>
    /// High-quality, fast downscale: halves (a bilinear sample at exactly half size averages 2 × 2 pixels, i.e. a
    /// box filter) while the image is more than twice the target, then one bilinear step. ~4× faster than
    /// building mipmaps of the full image.
    /// </summary>
    public static SKBitmap Downscale(SKBitmap source, int width, int height)
    {
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        SKBitmap current = source;
        while (current.Width >= 2 * width && current.Height >= 2 * height)
        {
            var half = current.Resize(new SKImageInfo(current.Width / 2, current.Height / 2, SKColorType.Rgba8888, SKAlphaType.Premul), sampling)
                ?? throw new InvalidOperationException("Could not create preview image.");
            if (!ReferenceEquals(current, source))
                current.Dispose();
            current = half;
        }
        if (current.Width == width && current.Height == height)
            return ReferenceEquals(current, source) ? source.Copy() : current;
        var result = current.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), sampling)
            ?? throw new InvalidOperationException("Could not create preview image.");
        if (!ReferenceEquals(current, source))
            current.Dispose();
        return result;
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
