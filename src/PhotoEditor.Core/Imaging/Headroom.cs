using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// The part of a RAW photo above white: the 8-bit photo stores each channel clipped at 1, and this layer stores
/// how far it really went (in the photo's encoded units, 1 = white), so Exposure / Highlights can bring back
/// highlights the sensor recorded. Channel value = 1 + <see cref="Scale"/> × stored / 255 where the photo is at
/// 255; 0 elsewhere.
/// <para>
/// A photo carries its layer as an attachment (<see cref="Of"/>): the decoded bitmap, the images of its
/// <see cref="PreviewImage"/> and the AI-restored working copies. Photos without one (JPEG etc.) render as before.
/// </para>
/// </summary>
public sealed class Headroom
{
    public Headroom(SKBitmap bitmap, float scale)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888)
            throw new ArgumentException("The headroom layer must be RGBA8888.", nameof(bitmap));
        bitmap.SetImmutable();
        Bitmap = bitmap;
        Scale = scale;
    }

    /// <summary>
    /// A layer whose bitmap may still change (spot removal updates it in place); <paramref name="image"/> shows its
    /// current pixels.
    /// </summary>
    public Headroom(SKBitmap bitmap, float scale, SKImage image)
    {
        Bitmap = bitmap;
        Scale = scale;
        _image = image;
    }

    /// <summary>RGBA8888, opaque; red / green / blue = how far the channel goes above white (0..255 = 0..<see cref="Scale"/>).</summary>
    public SKBitmap Bitmap { get; }

    /// <summary>The brightest value the layer can hold, minus 1 (encoded units).</summary>
    public float Scale { get; }

    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    /// <summary>The layer as an image for the shader (made on first use).</summary>
    public SKImage Image => _image ??= SKImage.FromBitmap(Bitmap);
    private SKImage? _image;

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

    /// <summary>The layer at another size (for a preview): averaging the stored values averages the real ones.</summary>
    public Headroom Resized(int width, int height) =>
        width == Width && height == Height ? this : new Headroom(PreviewImage.Downscale(Bitmap, width, height), Scale);

    /// <summary>What the layer adds to a channel stored as <paramref name="stored"/> (0..255).</summary>
    public float Extra(byte stored) => stored * Scale / 255f;
}
