using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Retouch;

/// <summary>
/// Brush-shaped heal / clone spots (a painted <see cref="Spot.Path"/> with a source offset
/// <see cref="Spot.Source"/> − <see cref="Spot.Center"/>), e.g. along a wire. Clone copies the stroke-shaped area
/// beside it; heal adds a membrane like the circles do, but since a stroke has no closed-form harmonic
/// interpolation, the destination − source differences on the stroke's outline are interpolated with
/// inverse-distance weights (smooth inside, equal to the outline's differences at the edge).
/// </summary>
public static partial class Retouching
{
    /// <summary>A heal / clone spot painted as a stroke (more than one point).</summary>
    public static bool IsStroke(Spot spot) => spot.Mode != SpotMode.Remove && spot.Path.Count > 1;

    /// <summary>The source offset of a spot in pixels.</summary>
    private static (float X, float Y) Offset(Spot spot, int w, int h) =>
        ((spot.Source.X - spot.Center.X) * w, (spot.Source.Y - spot.Center.Y) * h);

    private static SKRectI Shifted(SKRectI r, float dx, float dy, int margin, int w, int h) => SKRectI.Intersect(
        new SKRectI((int)MathF.Floor(r.Left + dx) - margin, (int)MathF.Floor(r.Top + dy) - margin,
            (int)MathF.Ceiling(r.Right + dx) + margin, (int)MathF.Ceiling(r.Bottom + dy) + margin),
        new SKRectI(0, 0, w, h));

    /// <summary>Pixels a stroke spot reads: its own area with the outline ring, and the same shape at the source.</summary>
    private static (SKRectI Destination, SKRectI Source) StrokeReadBounds(Spot spot, int w, int h)
    {
        var own = RemoveFill.Bounds(spot, w, h);
        var (ox, oy) = Offset(spot, w, h);
        return (Shifted(own, 0, 0, 4, w, h), Shifted(own, ox, oy, 4, w, h));
    }

    /// <summary>
    /// Points on the stroke's outline, just outside it: along both sides of each segment (about every half radius)
    /// and around the ends; points that fall inside another part of the stroke are dropped.
    /// </summary>
    private static List<SKPoint> Outline(SKPoint[] points, float radius, float distance)
    {
        var outline = new List<SKPoint>();
        float step = MathF.Max(radius / 2, 1.5f);
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3f)
                continue;
            float nx = -dy / len, ny = dx / len;
            int n = Math.Max(1, (int)(len / step));
            for (int k = 0; k <= n; k++)
            {
                float t = (float)k / n;
                float x = a.X + dx * t, y = a.Y + dy * t;
                outline.Add(new SKPoint(x + nx * distance, y + ny * distance));
                outline.Add(new SKPoint(x - nx * distance, y - ny * distance));
            }
        }
        // Round ends
        foreach (var end in new[] { points[0], points[^1] })
            for (int k = 0; k < 16; k++)
            {
                double angle = 2 * Math.PI * k / 16;
                outline.Add(new SKPoint(end.X + distance * (float)Math.Cos(angle), end.Y + distance * (float)Math.Sin(angle)));
            }
        return outline.Where(p => RemoveFill.Coverage(points, distance - 0.5f, p.X, p.Y) <= 0f).ToList();
    }

    private static void ApplyStrokeSpot(SKBitmap bitmap, Spot spot)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var (points, radius) = RemoveFill.Stroke(spot, w, h);
        var (ox, oy) = Offset(spot, w, h);
        var (dest, source) = StrokeReadBounds(spot, w, h);
        var region = SKRectI.Union(dest, source);
        if (region.IsEmpty)
            return;
        var snapshot = new Snapshot(bitmap, region);

        // Heal: destination − source on the outline (3 distances averaged), per channel.
        List<SKPoint> outline = [];
        var differences = new List<float[]>();
        if (spot.Mode == SpotMode.Heal)
        {
            outline = Outline(points, radius, radius + 2);
            foreach (var b in outline)
            {
                var d = new float[4];
                // average over a little ring around the outline point
                for (int j = -1; j <= 1; j++)
                {
                    double x = b.X + 0.5 + j, y = b.Y + 0.5;
                    var dp = snapshot.Sample(x, y);
                    var sp = snapshot.Sample(x + ox, y + oy);
                    for (int ch = 0; ch < 4; ch++)
                        d[ch] += (dp[ch] - sp[ch]) / 3f;
                }
                differences.Add(d);
            }
        }

        var bounds = RemoveFill.Bounds(spot, w, h);
        float inner = radius * (1 - spot.Feather);
        unsafe
        {
            byte* pixels = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float alpha = FeatheredCoverage(points, radius, inner, px, py) * spot.Opacity;
                    if (alpha <= 0)
                        continue;
                    var s = snapshot.Sample(px + ox, py + oy);
                    if (outline.Count > 0)
                    {
                        // Inverse-distance (Shepard) interpolation of the outline's differences.
                        double s0 = 0, s1 = 0, s2 = 0, wsum = 0;
                        for (int k = 0; k < outline.Count; k++)
                        {
                            double ddx = outline[k].X - px, ddy = outline[k].Y - py;
                            double wk = 1 / (ddx * ddx + ddy * ddy + 0.25);
                            wk *= wk; // 1 / d⁴: close outline points dominate, smooth inside
                            s0 += wk * differences[k][0];
                            s1 += wk * differences[k][1];
                            s2 += wk * differences[k][2];
                            wsum += wk;
                        }
                        s[0] += (float)(s0 / wsum);
                        s[1] += (float)(s1 / wsum);
                        s[2] += (float)(s2 / wsum);
                    }
                    byte* p = pixels + (long)y * rowBytes + x * 4;
                    float a8 = p[3];
                    for (int ch = 0; ch < 3; ch++)
                        p[ch] = (byte)Math.Clamp(Math.Round(p[ch] + alpha * (s[ch] - p[ch])), 0, a8);
                }
            }
        }
    }

    /// <summary>Stroke coverage with the spot's own feather (full inside <paramref name="inner"/>, 0 at the radius).</summary>
    private static float FeatheredCoverage(SKPoint[] points, float radius, float inner, float x, float y)
    {
        // distance to the path from the hard-edged coverage at the full radius
        float best = float.MaxValue;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[Math.Min(i + 1, points.Length - 1)];
            float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            float t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0f, 1f) : 0f;
            float qx = a.X + t * dx - x, qy = a.Y + t * dy - y;
            best = MathF.Min(best, qx * qx + qy * qy);
        }
        float d = MathF.Sqrt(best);
        if (d <= inner)
            return 1f;
        if (d >= radius)
            return 0f;
        float u = (d - inner) / MathF.Max(radius - inner, 1e-3f);
        return 1f - u * u * (3 - 2 * u);
    }

    /// <summary>
    /// A good source offset for a painted stroke: among shifts of 2.5–6 brush radii in 16 directions that keep the
    /// shape inside the photo and clear of the stroke itself (at least a brush width apart), the one whose outline looks most like the stroke's outline and whose inside is as
    /// plain as that outline. Returns the source centre for a spot centred at the stroke's <paramref name="center"/>.
    /// </summary>
    public static BrushPoint FindStrokeSource(SKBitmap image, IReadOnlyList<BrushPoint> path, float radius, BrushPoint center)
    {
        using var converted = image.ColorType == SKColorType.Rgba8888 ? null : image.Copy(SKColorType.Rgba8888);
        var bitmap = converted ?? image;
        int w = bitmap.Width, h = bitmap.Height;
        var spot = new Spot { Path = [.. path], Radius = radius, Center = center };
        var (points, r) = RemoveFill.Stroke(spot, w, h);
        var all = new Snapshot(bitmap, new SKRectI(0, 0, w, h), copy: false);
        var outline = Outline(points, r, r * 1.4f);
        if (outline.Count > 400)
            outline = outline.Where((_, i) => i % (outline.Count / 400 + 1) == 0).ToList();
        var inside = points.Where((_, i) => i % Math.Max(1, points.Length / 60) == 0).ToList();
        var bounds = RemoveFill.Bounds(spot, w, h);
        double Lum(double x, double y) => Luminance(all.Sample(x, y));
        var reference = outline.Select(p => Lum(p.X, p.Y)).ToArray();

        // The stroke's main direction (from its end points): shifting across it keeps whatever crosses the stroke
        // (a ledge, a horizon) lined up; shifting along it copies those to the wrong place.
        float ax = points[^1].X - points[0].X, ay = points[^1].Y - points[0].Y, length = MathF.Sqrt(ax * ax + ay * ay);
        (float X, float Y) direction = length > 2 * r ? (ax / length, ay / length) : (0, 0);
        (float X, float Y) best = (3 * r, 0);
        double bestScore = double.MaxValue;
        foreach (double distance in new[] { 2.5, 3.5, 4.5, 6 })
        {
            for (int k = 0; k < 16; k++)
            {
                double a = 2 * Math.PI * k / 16;
                float dx = (float)(distance * r * Math.Cos(a)), dy = (float)(distance * r * Math.Sin(a));
                bool fits = bounds.Left + dx >= 0 && bounds.Top + dy >= 0 && bounds.Right + dx <= w && bounds.Bottom + dy <= h;
                double score = 0;
                for (int i = 0; i < outline.Count; i++)
                {
                    double v = Lum(outline[i].X + dx, outline[i].Y + dy) - reference[i];
                    score += v * v;
                }
                score /= Math.Max(1, outline.Count);
                var values = inside.Select(p => Lum(p.X + dx, p.Y + dy)).ToList();
                var ring = outline.Select(p => Lum(p.X + dx, p.Y + dy)).ToList();
                var (innerMean, innerSpread) = MeanAndSpread(values);
                var (ringMean, ringSpread) = MeanAndSpread(ring);
                score += 2 * (innerMean - ringMean) * (innerMean - ringMean) + Math.Max(0, innerSpread - ringSpread) * 2;
                score *= 1 + 0.02 * distance;
                double along = (dx * direction.X + dy * direction.Y) / (distance * r);
                score *= 1 + 4 * along * along;
                if (!fits)
                    score = score * 4 + 1e4;
                // The copied strip must not overlap the stroke (it would copy what lies along it, e.g. the rest of
                // the wire or its neighbours).
                if (points.Any(p => RemoveFill.Coverage(points, 2.1f * r, p.X + dx, p.Y + dy) > 0f))
                    score = score * 4 + 1e4;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = (dx, dy);
                }
            }
        }
        return new BrushPoint(Math.Clamp(center.X + best.X / w, 0f, 1f), Math.Clamp(center.Y + best.Y / h, 0f, 1f));
    }
}
