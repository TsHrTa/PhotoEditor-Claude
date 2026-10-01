using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Editing;

/// <summary>
/// The user's rotation / flip of the finished picture: turned <see cref="QuarterTurns"/> × 90° clockwise, then
/// mirrored left-right when <see cref="Flip"/>. Applied last (after adjustments and crop); masks and the crop stay
/// defined on the unrotated photo, so rotating never has to convert them.
/// </summary>
public readonly record struct PhotoOrientation(int QuarterTurns = 0, bool Flip = false)
{
    public static readonly PhotoOrientation None = new();

    /// <summary>Quarter turns normalised to 0..3.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Turns => ((QuarterTurns % 4) + 4) % 4;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNone => Turns == 0 && !Flip;

    /// <summary>Width and height trade places (90° or 270°).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SwapsSides => Turns % 2 == 1;

    // Derivations, with D = M^flip · R^turns (R = 90° clockwise, M = mirror) and M·R = R⁻¹·M:
    //   R·D = M^flip · R^(turns ± 1),  M·D = M^(1−flip) · R^turns,  vertical flip V = M·R²: V·D = M^(1−flip) · R^(turns+2).

    /// <summary>The picture as shown, turned 90° clockwise.</summary>
    public PhotoOrientation RotatedClockwise() => new((Turns + (Flip ? 3 : 1)) % 4, Flip);

    /// <summary>The picture as shown, turned 90° counter-clockwise.</summary>
    public PhotoOrientation RotatedCounterClockwise() => new((Turns + (Flip ? 1 : 3)) % 4, Flip);

    /// <summary>The picture as shown, mirrored left-right.</summary>
    public PhotoOrientation FlippedHorizontally() => new(Turns, !Flip);

    /// <summary>The picture as shown, mirrored top-bottom.</summary>
    public PhotoOrientation FlippedVertically() => new((Turns + 2) % 4, !Flip);

    /// <summary>Size of the oriented picture.</summary>
    public (int Width, int Height) OutputSize(int width, int height) => SwapsSides ? (height, width) : (width, height);

    /// <summary>Returns the oriented picture (the input itself when there is nothing to do).</summary>
    public SKBitmap Apply(SKBitmap source) => IsNone ? source : ImageLoader.ApplyOrientation(source, ToOrigin());

    /// <summary>The same transform as an EXIF orientation.</summary>
    public SKEncodedOrigin ToOrigin() => (Turns, Flip) switch
    {
        (0, false) => SKEncodedOrigin.TopLeft,
        (0, true) => SKEncodedOrigin.TopRight,
        (2, false) => SKEncodedOrigin.BottomRight,
        (2, true) => SKEncodedOrigin.BottomLeft,
        (1, true) => SKEncodedOrigin.LeftTop,
        (1, false) => SKEncodedOrigin.RightTop,
        (3, true) => SKEncodedOrigin.RightBottom,
        _ => SKEncodedOrigin.LeftBottom,
    };

    public static PhotoOrientation FromOrigin(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => new(0, true),
        SKEncodedOrigin.BottomRight => new(2, false),
        SKEncodedOrigin.BottomLeft => new(2, true),
        SKEncodedOrigin.LeftTop => new(1, true),
        SKEncodedOrigin.RightTop => new(1, false),
        SKEncodedOrigin.RightBottom => new(3, true),
        SKEncodedOrigin.LeftBottom => new(3, false),
        _ => None,
    };

    /// <summary>This orientation applied after <paramref name="first"/>.</summary>
    public PhotoOrientation After(PhotoOrientation first)
    {
        // M^f1 R^k1 · M^f0 R^k0 = M^(f1 xor f0) R^(±k1 + k0)
        int k = (first.Flip ? -Turns : Turns) + first.Turns;
        return new(((k % 4) + 4) % 4, Flip ^ first.Flip);
    }

    /// <summary>The orientation that undoes this one.</summary>
    public PhotoOrientation Inverse()
    {
        foreach (var candidate in All)
            if (candidate.After(this).IsNone)
                return candidate;
        return None;
    }

    public static IEnumerable<PhotoOrientation> All =>
        from turns in Enumerable.Range(0, 4) from flip in new[] { false, true } select new PhotoOrientation(turns, flip);
}
