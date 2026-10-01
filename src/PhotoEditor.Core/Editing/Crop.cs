using System.Text.Json.Serialization;

namespace PhotoEditor.Core.Editing;

/// <summary>
/// Crop of the (upright) image, stored like Lightroom: the edges of the unrotated crop rectangle
/// in normalised image coordinates (0..1 across width and height), and a straighten angle by which
/// that rectangle is rotated about its centre. The output is the rotated rectangle, axis-aligned.
/// </summary>
public sealed record Crop
{
    public static readonly Crop None = new();

    /// <summary>Largest straighten angle in degrees (either direction).</summary>
    public const double MaxAngle = 45;

    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; } = 1;
    public double Bottom { get; init; } = 1;

    /// <summary>Degrees; positive rotates the crop frame clockwise over the image (the result turns counter-clockwise).</summary>
    public double Angle { get; init; }

    [JsonIgnore]
    public bool IsDefault => this == None;

    /// <summary>The crop in pixels of a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public CropFrame Frame(double width, double height) => new(
        (Left + Right) / 2 * width, (Top + Bottom) / 2 * height,
        (Right - Left) / 2 * width, (Bottom - Top) / 2 * height, Angle);

    /// <summary>Output size in pixels for an image of the given size (at least 1 × 1).</summary>
    public (int Width, int Height) OutputSize(int width, int height) =>
        (Math.Max(1, (int)Math.Round((Right - Left) * width)), Math.Max(1, (int)Math.Round((Bottom - Top) * height)));

    public static Crop FromFrame(CropFrame f, double width, double height) => new()
    {
        Left = (f.CenterX - f.HalfWidth) / width,
        Right = (f.CenterX + f.HalfWidth) / width,
        Top = (f.CenterY - f.HalfHeight) / height,
        Bottom = (f.CenterY + f.HalfHeight) / height,
        Angle = f.Angle,
    };
}

/// <summary>
/// A crop rectangle in image pixels: centre, half size along its own (rotated) axes and angle.
/// "Local" coordinates are offsets from the centre along those axes.
/// </summary>
public readonly record struct CropFrame(double CenterX, double CenterY, double HalfWidth, double HalfHeight, double Angle)
{
    public double Cos => Math.Cos(Angle * Math.PI / 180);
    public double Sin => Math.Sin(Angle * Math.PI / 180);

    /// <summary>Image position of the local point (<paramref name="u"/>, <paramref name="v"/>).</summary>
    public (double X, double Y) ToImage(double u, double v)
    {
        double c = Cos, s = Sin;
        return (CenterX + u * c - v * s, CenterY + u * s + v * c);
    }

    /// <summary>Local coordinates of the image position (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public (double U, double V) ToLocal(double x, double y)
    {
        double c = Cos, s = Sin, dx = x - CenterX, dy = y - CenterY;
        return (dx * c + dy * s, -dx * s + dy * c);
    }

    /// <summary>Corners in image pixels: top-left, top-right, bottom-right, bottom-left (of the rotated frame).</summary>
    public (double X, double Y)[] Corners() =>
    [
        ToImage(-HalfWidth, -HalfHeight), ToImage(HalfWidth, -HalfHeight),
        ToImage(HalfWidth, HalfHeight), ToImage(-HalfWidth, HalfHeight),
    ];

    /// <summary>True when the whole rotated frame lies within the image.</summary>
    public bool IsInside(double width, double height)
    {
        const double eps = 1e-6;
        foreach (var (x, y) in Corners())
        {
            if (x < -eps || y < -eps || x > width + eps || y > height + eps)
                return false;
        }
        return true;
    }

    public static CropFrame Lerp(CropFrame a, CropFrame b, double t) => new(
        a.CenterX + (b.CenterX - a.CenterX) * t, a.CenterY + (b.CenterY - a.CenterY) * t,
        a.HalfWidth + (b.HalfWidth - a.HalfWidth) * t, a.HalfHeight + (b.HalfHeight - a.HalfHeight) * t,
        a.Angle + (b.Angle - a.Angle) * t);
}

/// <summary>What part of the crop rectangle is dragged.</summary>
[Flags]
public enum CropHandle
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
    Move = 16,
}

/// <summary>Pure crop editing operations; results always stay inside the image.</summary>
public static class CropGeometry
{
    /// <summary>Smallest crop side as a fraction of the image's shorter side.</summary>
    public const double MinSizeFraction = 0.02;

    /// <summary>
    /// Result of dragging <paramref name="handle"/> of <paramref name="start"/> from <paramref name="from"/> to
    /// <paramref name="to"/> (normalised image coordinates). <paramref name="aspect"/> = locked width / height in
    /// pixels, or null for free.
    /// </summary>
    public static Crop Drag(Crop start, CropHandle handle, (double X, double Y) from, (double X, double Y) to,
        double? aspect, int width, int height)
    {
        var f = start.Frame(width, height);
        double dx = (to.X - from.X) * width, dy = (to.Y - from.Y) * height;

        if (handle == CropHandle.Move)
        {
            // Slide along the border: constrain x and y separately.
            var moved = Constrain(f, f with { CenterX = f.CenterX + dx }, width, height);
            moved = Constrain(moved, moved with { CenterY = moved.CenterY + dy }, width, height);
            return Crop.FromFrame(moved, width, height);
        }

        double du = dx * f.Cos + dy * f.Sin, dv = -dx * f.Sin + dy * f.Cos;
        double l = -f.HalfWidth, r = f.HalfWidth, t = -f.HalfHeight, b = f.HalfHeight;
        double min = MinSizeFraction * Math.Min(width, height);
        if (handle.HasFlag(CropHandle.Left)) l = Math.Min(l + du, r - min);
        if (handle.HasFlag(CropHandle.Right)) r = Math.Max(r + du, l + min);
        if (handle.HasFlag(CropHandle.Top)) t = Math.Min(t + dv, b - min);
        if (handle.HasFlag(CropHandle.Bottom)) b = Math.Max(b + dv, t + min);

        if (aspect is > 0 and var ratio)
        {
            bool horizontal = handle.HasFlag(CropHandle.Left) || handle.HasFlag(CropHandle.Right);
            bool vertical = handle.HasFlag(CropHandle.Top) || handle.HasFlag(CropHandle.Bottom);
            double w = r - l, h = b - t;
            if (horizontal && vertical)
            {
                // Corner: grow to cover the pointer, keep the opposite corner in place.
                if (w / ratio > h) h = w / ratio; else w = h * ratio;
                if (handle.HasFlag(CropHandle.Left)) l = r - w; else r = l + w;
                if (handle.HasFlag(CropHandle.Top)) t = b - h; else b = t + h;
            }
            else if (horizontal)
            {
                h = w / ratio;
                double cy = (t + b) / 2;
                (t, b) = (cy - h / 2, cy + h / 2);
            }
            else
            {
                w = h * ratio;
                double cx = (l + r) / 2;
                (l, r) = (cx - w / 2, cx + w / 2);
            }
        }

        var (cxImg, cyImg) = f.ToImage((l + r) / 2, (t + b) / 2);
        var proposed = new CropFrame(cxImg, cyImg, (r - l) / 2, (b - t) / 2, f.Angle);
        return Crop.FromFrame(Constrain(f, proposed, width, height), width, height);
    }

    /// <summary>
    /// The frame closest to <paramref name="proposed"/> on the way from <paramref name="valid"/> that is
    /// inside the image (binary search; <paramref name="valid"/> is returned if even small steps leave it).
    /// </summary>
    public static CropFrame Constrain(CropFrame valid, CropFrame proposed, double width, double height)
    {
        if (proposed.IsInside(width, height))
            return proposed;
        double lo = 0, hi = 1;
        for (int i = 0; i < 30; i++)
        {
            double mid = (lo + hi) / 2;
            if (CropFrame.Lerp(valid, proposed, mid).IsInside(width, height)) lo = mid; else hi = mid;
        }
        return CropFrame.Lerp(valid, proposed, lo);
    }

    /// <summary>
    /// <paramref name="crop"/> rotated to <paramref name="angle"/> about its centre and shrunk (keeping its
    /// aspect ratio) just enough to stay inside the image.
    /// </summary>
    public static Crop WithAngle(Crop crop, double angle, int width, int height)
    {
        angle = Math.Clamp(angle, -Crop.MaxAngle, Crop.MaxAngle);
        var f = crop.Frame(width, height) with { Angle = angle };
        f = f with { CenterX = Math.Clamp(f.CenterX, 0, width), CenterY = Math.Clamp(f.CenterY, 0, height) };
        double scale = 1;
        foreach (var (u, v) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
        {
            var (x, y) = f.ToImage(u * f.HalfWidth, v * f.HalfHeight);
            double ox = x - f.CenterX, oy = y - f.CenterY;
            if (ox > 1e-9) scale = Math.Min(scale, (width - f.CenterX) / ox);
            if (ox < -1e-9) scale = Math.Min(scale, f.CenterX / -ox);
            if (oy > 1e-9) scale = Math.Min(scale, (height - f.CenterY) / oy);
            if (oy < -1e-9) scale = Math.Min(scale, f.CenterY / -oy);
        }
        scale = Math.Max(scale, 0);
        return Crop.FromFrame(f with { HalfWidth = f.HalfWidth * scale, HalfHeight = f.HalfHeight * scale }, width, height);
    }

    /// <summary>The largest crop with the given aspect ratio (width / height in pixels) inside <paramref name="crop"/>, same centre.</summary>
    public static Crop WithAspect(Crop crop, double aspect, int width, int height)
    {
        var f = crop.Frame(width, height);
        double w = f.HalfWidth, h = f.HalfHeight;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        return Crop.FromFrame(f with { HalfWidth = w, HalfHeight = h }, width, height);
    }

    /// <summary>Rotates the crop's aspect ratio by 90° (landscape ↔ portrait), largest size that fits.</summary>
    public static Crop SwapOrientation(Crop crop, int width, int height)
    {
        var f = crop.Frame(width, height);
        var swapped = f with { HalfWidth = f.HalfHeight, HalfHeight = f.HalfWidth };
        // Shrink about the centre until it fits (angle unchanged).
        return WithAngle(Crop.FromFrame(swapped, width, height), f.Angle, width, height);
    }

    /// <summary>Clamps values read from a file into a sane range; returns <see cref="Crop.None"/> if unusable.</summary>
    public static Crop Normalize(Crop? crop)
    {
        if (crop is null)
            return Crop.None;
        double[] edges = [crop.Left, crop.Top, crop.Right, crop.Bottom, crop.Angle];
        if (edges.Any(v => !double.IsFinite(v)) || crop.Right - crop.Left < 1e-4 || crop.Bottom - crop.Top < 1e-4)
            return Crop.None;
        return crop with
        {
            Left = Math.Clamp(crop.Left, -1, 2),
            Top = Math.Clamp(crop.Top, -1, 2),
            Right = Math.Clamp(crop.Right, -1, 2),
            Bottom = Math.Clamp(crop.Bottom, -1, 2),
            Angle = Math.Clamp(crop.Angle, -Crop.MaxAngle, Crop.MaxAngle),
        };
    }
}
