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

    /// <summary>Pixels the stroke covers (with its soft edge).</summary>
    public static SKRectI Bounds(Spot spot, int width, int height)
    {
        var (points, radius) = Stroke(spot, width, height);
        var r = SKRectI.Ceiling(new SKRect(points.Min(p => p.X) - radius - 1, points.Min(p => p.Y) - radius - 1,
            points.Max(p => p.X) + radius + 1, points.Max(p => p.Y) + radius + 1));
        return SKRectI.Intersect(r, new SKRectI(0, 0, width, height));
    }

    /// <summary>
    /// The square of photo around the stroke the model sees, in pixels (floating point, so it scales exactly with
    /// the image size): about 2.5 × the stroke's extent, at least 8 % of the long side, kept inside the photo.
    /// </summary>
    public static SKRect ContextRect(Spot spot, int width, int height)
    {
        var (points, radius) = Stroke(spot, width, height);
        float left = points.Min(p => p.X) - radius, right = points.Max(p => p.X) + radius;
        float top = points.Min(p => p.Y) - radius, bottom = points.Max(p => p.Y) + radius;
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
        var (points, radius) = Stroke(spot, photo.Width, photo.Height);
        float scale = ModelSize / rect.Width;
        var scaled = points.Select(p => new SKPoint((p.X - rect.Left) * scale, (p.Y - rect.Top) * scale)).ToArray();
        float holeRadius = radius * scale + 3;
        var hole = new bool[ModelSize * ModelSize];
        for (int y = 0; y < ModelSize; y++)
            for (int x = 0; x < ModelSize; x++)
                hole[y * ModelSize + x] = Coverage(scaled, holeRadius / (1 - EdgeFeather) , x + 0.5f, y + 0.5f) >= 1f;
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
        var (points, radius) = Stroke(spot, w, h);
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
                float alpha = Coverage(points, radius, x + 0.5f, y + 0.5f) * spot.Opacity;
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
