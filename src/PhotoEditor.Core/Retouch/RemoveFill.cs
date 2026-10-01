using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Retouch;

/// <summary>Which bitmap of a photo a spot is applied to: the photo, or one of its headroom layers.</summary>
public enum SpotLayer
{
    Photo,

    /// <summary>The part above white: removed areas get none.</summary>
    Above,

    /// <summary>The rounded-away fractions: removed areas get none.</summary>
    Fine,
}

/// <summary>
/// Geometry and rendering of AI Remove spots: the painted stroke's coverage, the square of surrounding photo the
/// inpainting model sees (<see cref="ContextRect"/>, shown to it at <see cref="ModelSize"/> px), and blending the
/// model's fill back in at any resolution. All sizes are relative to the photo, so the preview and the export use
/// the same fill in the same place.
/// </summary>
public static class RemoveFill
{
    /// <summary>The inpainting model's input and output size (LaMa: 512 × 512).</summary>
    public const int ModelSize = 512;

    /// <summary>Soft edge of a Remove stroke, as a fraction of its radius.</summary>
    public const float EdgeFeather = 0.25f;

    /// <summary>The stroke in pixels of a <paramref name="width"/> × <paramref name="height"/> image: points and radius.</summary>
    public static (SKPoint[] Points, float Radius) Stroke(Spot spot, int width, int height)
    {
        var points = spot.Path.Count > 0 ? spot.Path : [spot.Center];
        float radius = MathF.Max(1f, spot.Radius * Math.Max(width, height));
        return (points.Select(p => new SKPoint(p.X * width, p.Y * height)).ToArray(), radius);
    }

    /// <summary>One stroke of a selection in pixels.</summary>
    public readonly record struct PixelStroke(SKPoint[] Points, float Radius, bool Erase);

    /// <summary>The spot's strokes (<see cref="Spot.RemoveStrokes"/>) in pixels of a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public static PixelStroke[] Strokes(Spot spot, int width, int height)
    {
        float longSide = Math.Max(width, height);
        return spot.RemoveStrokes
            .Select(s => new PixelStroke(s.Path.Select(p => new SKPoint(p.X * width, p.Y * height)).ToArray(),
                MathF.Max(1f, s.Radius * longSide), s.Erase))
            .ToArray();
    }

    /// <summary>Extent of the painted (not erased) strokes, radius included.</summary>
    private static SKRect Extent(PixelStroke[] strokes)
    {
        var add = strokes.Where(s => !s.Erase).ToArray();
        if (add.Length == 0)
            return SKRect.Empty;
        return new SKRect(add.Min(s => s.Points.Min(p => p.X) - s.Radius), add.Min(s => s.Points.Min(p => p.Y) - s.Radius),
            add.Max(s => s.Points.Max(p => p.X) + s.Radius), add.Max(s => s.Points.Max(p => p.Y) + s.Radius));
    }

    /// <summary>Pixels the stroke(s) cover (with the soft edge).</summary>
    public static SKRectI Bounds(Spot spot, int width, int height)
    {
        var e = Extent(Strokes(spot, width, height));
        if (e.IsEmpty)
            return SKRectI.Empty;
        var r = SKRectI.Ceiling(new SKRect(e.Left - 1, e.Top - 1, e.Right + 1, e.Bottom + 1));
        return SKRectI.Intersect(r, new SKRectI(0, 0, width, height));
    }

    /// <summary>
    /// The square of photo around the stroke(s) the model sees, in pixels (floating point, so it scales exactly with
    /// the image size): about 2.5 × the selection's extent, at least 8 % of the long side, kept inside the photo.
    /// </summary>
    public static SKRect ContextRect(Spot spot, int width, int height)
    {
        var e = Extent(Strokes(spot, width, height));
        float left = e.Left, right = e.Right, top = e.Top, bottom = e.Bottom;
        float longSide = Math.Max(width, height);
        float side = MathF.Max(MathF.Max(right - left, bottom - top) * 2.5f, 0.08f * longSide);
        side = MathF.Min(side, Math.Min(width, height));
        float cx = (left + right) / 2, cy = (top + bottom) / 2;
        float x0 = Math.Clamp(cx - side / 2, 0, width - side), y0 = Math.Clamp(cy - side / 2, 0, height - side);
        return new SKRect(x0, y0, x0 + side, y0 + side);
    }

    /// <summary>Coverage 0..1 of the stroke at a point (distance to the path, soft edge).</summary>
    public static float Coverage(SKPoint[] points, float radius, float x, float y)
    {
        float best = float.MaxValue;
        if (points.Length == 1)
            best = Distance2(points[0], x, y);
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len2 = dx * dx + dy * dy;
            float t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0f, 1f) : 0f;
            float px = a.X + t * dx - x, py = a.Y + t * dy - y;
            best = MathF.Min(best, px * px + py * py);
        }
        float d = MathF.Sqrt(best);
        float inner = radius * (1 - EdgeFeather);
        if (d <= inner)
            return 1f;
        if (d >= radius)
            return 0f;
        float u = (d - inner) / (radius - inner);
        return 1f - u * u * (3 - 2 * u);
    }

    /// <summary>Coverage 0..1 of a selection at a point: the painted strokes minus the erased ones.</summary>
    public static float Coverage(PixelStroke[] strokes, float x, float y)
    {
        float add = 0, erase = 0;
        foreach (var s in strokes)
        {
            if (s.Erase ? erase >= 1f : add >= 1f)
                continue;
            float c = Coverage(s.Points, s.Radius, x, y);
            if (s.Erase)
                erase = MathF.Max(erase, c);
            else
                add = MathF.Max(add, c);
        }
        return add * (1f - erase);
    }

    private static float Distance2(SKPoint p, float x, float y) => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);

    /// <summary>
    /// The model's input: the context square of <paramref name="photo"/> at <see cref="ModelSize"/> px and the hole
    /// (true = to fill; the stroke plus a 3-px margin at model scale, so the object's edge is filled too).
    /// </summary>
    public static (SKBitmap Image, bool[] Hole) ModelInput(SKBitmap photo, Spot spot)
    {
        var rect = ContextRect(spot, photo.Width, photo.Height);
        var image = new SKBitmap(new SKImageInfo(ModelSize, ModelSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(image))
        using (var source = SKImage.FromBitmap(photo))
        using (var paint = new SKPaint())
        {
            canvas.DrawImage(source, rect, new SKRect(0, 0, ModelSize, ModelSize),
                new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        float scale = ModelSize / rect.Width;
        // Coverage(…, R) ≥ 1 exactly within R · (1 − EdgeFeather) of the path: radii chosen so the hole reaches 3 px
        // past each painted stroke and 3 px into each erased one (its soft edge still blends in some fill).
        var strokes = Strokes(spot, photo.Width, photo.Height)
            .Select(s => new PixelStroke(s.Points.Select(p => new SKPoint((p.X - rect.Left) * scale, (p.Y - rect.Top) * scale)).ToArray(),
                s.Erase ? (s.Radius * scale * (1 - EdgeFeather) - 3) / (1 - EdgeFeather) : (s.Radius * scale + 3) / (1 - EdgeFeather),
                s.Erase))
            .ToArray();
        var hole = new bool[ModelSize * ModelSize];
        for (int y = 0; y < ModelSize; y++)
            for (int x = 0; x < ModelSize; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                hole[y * ModelSize + x] =
                    strokes.Any(s => !s.Erase && Coverage(s.Points, s.Radius, px, py) >= 1f)
                    && !strokes.Any(s => s.Erase && s.Radius > 0 && Coverage(s.Points, s.Radius, px, py) >= 1f);
            }
        return (image, hole);
    }

    /// <summary>
    /// Blends the spot's fill into <paramref name="bitmap"/> (RGBA8888, the photo or one of its layers) inside the
    /// stroke. Returns false when the fill is not available.
    /// </summary>
    public static unsafe bool Apply(SKBitmap bitmap, Spot spot, SpotLayer layer)
    {
        var fill = FillStore.Get(spot.Fill);
        if (fill is null)
            return false;
        int w = bitmap.Width, h = bitmap.Height;
        var strokes = Strokes(spot, w, h);
        var rect = ContextRect(spot, w, h);
        var bounds = Bounds(spot, w, h);
        float scale = fill.Width / rect.Width;
        byte* pixels = (byte*)bitmap.GetPixels();
        byte* fillPixels = (byte*)fill.GetPixels();
        int rowBytes = bitmap.RowBytes, fillRowBytes = fill.RowBytes, fw = fill.Width, fh = fill.Height;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                float alpha = Coverage(strokes, x + 0.5f, y + 0.5f) * spot.Opacity;
                if (alpha <= 0)
                    continue;
                byte* p = pixels + (long)y * rowBytes + x * 4;
                if (layer != SpotLayer.Photo)
                {
                    byte target = layer == SpotLayer.Fine ? (byte)128 : (byte)0;
                    for (int ch = 0; ch < 3; ch++)
                        p[ch] = (byte)MathF.Round(p[ch] + alpha * (target - p[ch]));
                    continue;
                }
                // Bilinear sample of the fill at this pixel's place in the context square.
                float u = (x + 0.5f - rect.Left) * scale - 0.5f, v = (y + 0.5f - rect.Top) * scale - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(u), 0, fw - 1), y0 = Math.Clamp((int)MathF.Floor(v), 0, fh - 1);
                int x1 = Math.Min(x0 + 1, fw - 1), y1 = Math.Min(y0 + 1, fh - 1);
                float fx = Math.Clamp(u - x0, 0, 1), fy = Math.Clamp(v - y0, 0, 1);
                byte* a = fillPixels + (long)y0 * fillRowBytes, b = fillPixels + (long)y1 * fillRowBytes;
                float a8 = p[3];
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = a[x0 * 4 + ch] + (a[x1 * 4 + ch] - a[x0 * 4 + ch]) * fx;
                    float bottom = b[x0 * 4 + ch] + (b[x1 * 4 + ch] - b[x0 * 4 + ch]) * fx;
                    float value = (top + (bottom - top) * fy) * a8 / 255f; // premultiplied
                    p[ch] = (byte)Math.Clamp(MathF.Round(p[ch] + alpha * (value - p[ch])), 0, a8);
                }
            }
        }
        return true;
    }
}
