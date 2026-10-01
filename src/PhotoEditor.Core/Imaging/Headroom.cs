using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// What a RAW's 8-bit photo cannot hold, as layers next to it (in the photo's encoded units, 1 = white):
/// <list type="bullet">
/// <item><see cref="Bitmap"/>: how far each channel really went above white, so Exposure / Highlights can bring
/// back highlights the sensor recorded. Channel value = 1 + <see cref="Scale"/> × stored / 255 where the photo is at
/// 255; 0 elsewhere.</item>
/// <item><see cref="Fine"/> (optional): the fraction of an 8-bit step the photo rounded away — true value = photo +
/// (stored − 128) / (255 × 255) — so shadows lifted a lot keep smooth gradients (≈ 16-bit precision).</item>
/// </list>
/// A photo carries its layers as an attachment (<see cref="Of"/>): the decoded bitmap, the images of its
/// <see cref="PreviewImage"/>, the retouched and AI-restored copies (those without <see cref="Fine"/>: the AI
/// changes the pixels, so the fraction no longer applies). Photos without one (JPEG etc.) render as before.
/// </summary>
public sealed class Headroom
{
    /// <summary>Stored value of <see cref="Fine"/> meaning "nothing to add".</summary>
    public const byte FineZero = 128;

    /// <summary>One stored step of <see cref="Fine"/> in encoded units (1/255 of an 8-bit step).</summary>
    public const float FineStep = 1f / (255f * 255f);

    public Headroom(SKBitmap bitmap, float scale, SKBitmap? fine = null)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888 || fine is { ColorType: not SKColorType.Rgba8888 })
            throw new ArgumentException("The headroom layers must be RGBA8888.", nameof(bitmap));
        if (fine is not null && (fine.Width != bitmap.Width || fine.Height != bitmap.Height))
            throw new ArgumentException("The headroom layers must have the same size.", nameof(fine));
        bitmap.SetImmutable();
        fine?.SetImmutable();
        Bitmap = bitmap;
        Scale = scale;
        Fine = fine;
    }

    /// <summary>
    /// Layers whose bitmaps may still change (spot removal updates them in place); the images show their current
    /// pixels.
    /// </summary>
    public Headroom(SKBitmap bitmap, float scale, SKImage image, SKBitmap? fine, SKImage? fineImage)
    {
        Bitmap = bitmap;
        Scale = scale;
        _image = image;
        Fine = fine;
        _fineImage = fineImage;
    }

    /// <summary>
    /// The photo was rendered with <see cref="Adjustments.RawBaseCurve"/> (a RAW decoded by LibRaw directly): the
    /// renderers then apply Exposure before that curve, as Lightroom does.
    /// </summary>
    public bool BaseCurve { get; init; }

    /// <summary>RGBA8888, opaque; red / green / blue = how far the channel goes above white (0..255 = 0..<see cref="Scale"/>).</summary>
    public SKBitmap Bitmap { get; }

    /// <summary>The brightest value the layer can hold, minus 1 (encoded units).</summary>
    public float Scale { get; }

    /// <summary>RGBA8888, opaque; red / green / blue = the rounded-away fraction (<see cref="FineZero"/> = none), or null.</summary>
    public SKBitmap? Fine { get; }

    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    /// <summary>The layer as an image for the shader (made on first use).</summary>
    public SKImage Image => _image ??= SKImage.FromBitmap(Bitmap);
    private SKImage? _image;

    /// <summary>The fine layer as an image for the shader, or null.</summary>
    public SKImage? FineImage => Fine is null ? null : _fineImage ??= SKImage.FromBitmap(Fine);
    private SKImage? _fineImage;

    private static readonly ConditionalWeakTable<object, Headroom> Attached = new();

    /// <summary>The headroom of a photo (an <see cref="SKBitmap"/> or <see cref="SKImage"/>), or null.</summary>
    public static Headroom? Of(object? photo) =>
        photo is not null && Attached.TryGetValue(photo, out var headroom) ? headroom : null;

    /// <summary>Attaches <paramref name="headroom"/> to <paramref name="photo"/> (no-op for null).</summary>
    public static void Attach(object photo, Headroom? headroom)
    {
        if (headroom is not null)
            Attached.AddOrUpdate(photo, headroom);
    }

    /// <summary>The layers at another size (for a preview): averaging the stored values averages the real ones.</summary>
    public Headroom Resized(int width, int height) =>
        width == Width && height == Height
            ? this
            : new Headroom(PreviewImage.Downscale(Bitmap, width, height), Scale,
                Fine is null ? null : PreviewImage.Downscale(Fine, width, height)) { BaseCurve = BaseCurve };

    /// <summary>Only the part above white (for pixels the AI has changed).</summary>
    public Headroom WithoutFine() => Fine is null ? this : new Headroom(Bitmap, Scale) { BaseCurve = BaseCurve };

    /// <summary>What the layer adds to a channel stored as <paramref name="stored"/> (0..255).</summary>
    public float Extra(byte stored) => stored * Scale / 255f;

    /// <summary>What the fine layer adds to a channel stored as <paramref name="stored"/>.</summary>
    public static float FineOffset(byte stored) => (stored - FineZero) * FineStep;
}
