using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Retouch;

/// <summary>
/// Spot removal on the photo's pixels (before any adjustment, like Lightroom): each spot replaces a circle by
/// another part of the photo. Heal takes the source's texture and adds a smooth correction that makes it meet
/// the destination's surroundings exactly at the edge: the colour difference measured on a ring just outside
/// the circle, interpolated harmonically inside (Poisson kernel of the disk — the membrane of Poisson image
/// editing, in closed form). Clone copies the source.
/// </summary>
public static class Retouching
{
    /// <summary>Directions sampled on the boundary ring.</summary>
    private const int BoundarySamples = 64;

    /// <summary>
    /// A retouched copy of <paramref name="source"/> (RGBA8888 premultiplied), or <paramref name="source"/> itself
    /// when there are no spots. A <see cref="Headroom"/> of the source is retouched the same way and attached.
    /// </summary>
    public static SKBitmap Apply(SKBitmap source, IReadOnlyList<Spot> spots)
    {
        if (spots.Count == 0)
            return source;
        var result = source.Copy(SKColorType.Rgba8888) ?? throw new InvalidOperationException("Could not copy the photo.");
        Lens.LensVignetting.Attach(result, Lens.LensVignetting.Of(source));
        foreach (var spot in spots)
            ApplySpot(result, spot);
        if (Headroom.Of(source) is { } headroom && headroom.Width == source.Width && headroom.Height == source.Height)
        {
            var extra = headroom.Bitmap.Copy();
            var fine = headroom.Fine?.Copy();
            foreach (var spot in spots)
            {
                ApplySpot(extra, spot, SpotLayer.Above);
                if (fine is not null)
                    ApplySpot(fine, spot, SpotLayer.Fine);
            }
            Headroom.Attach(result, new Headroom(extra, headroom.Scale, fine));
        }
        return result;
    }

    /// <summary>Spot geometry in pixels of an image of the given size.</summary>
    private readonly record struct Circle(double X, double Y, double SourceX, double SourceY, double Radius)
    {
        public static Circle Of(Spot spot, int width, int height) => new(
            spot.Center.X * width, spot.Center.Y * height, spot.Source.X * width, spot.Source.Y * height,
            Math.Max(1.0, spot.Radius * Math.Max(width, height)));
    }

    /// <summary>The pixels a spot writes (its circle), in an image of the given size.</summary>
    public static SKRectI DestinationBounds(Spot spot, int width, int height)
    {
        if (spot.Mode == SpotMode.Remove)
            return RemoveFill.Bounds(spot, width, height);
        var c = Circle.Of(spot, width, height);
        return Bounds(c.X, c.Y, c.Radius, width, height);
    }

    /// <summary>The pixels a spot reads: its circle and its source, each with the ring around it.</summary>
    public static (SKRectI Destination, SKRectI Source) ReadBounds(Spot spot, int width, int height)
    {
        if (spot.Mode == SpotMode.Remove)
        {
            var own = RemoveFill.Bounds(spot, width, height); // the fill is stored; it only blends over its own pixels
            return (own, own);
        }
        var c = Circle.Of(spot, width, height);
        return (Bounds(c.X, c.Y, c.Radius + 4, width, height), Bounds(c.SourceX, c.SourceY, c.Radius + 4, width, height));
    }

    /// <summary>
    /// Applies one spot to <paramref name="bitmap"/> (RGBA8888) in place: the photo, or one of its headroom layers
    /// (<paramref name="layer"/>; heal / clone treat them like the photo, Remove clears them).
    /// </summary>
    public static void ApplySpot(SKBitmap bitmap, Spot spot, SpotLayer layer = SpotLayer.Photo)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888)
            throw new ArgumentException("Spot removal needs an RGBA8888 bitmap.", nameof(bitmap));
        if (spot.Mode == SpotMode.Remove)
            RemoveFill.Apply(bitmap, spot, layer);
        else
            ApplySpotCore(bitmap, spot);
    }

    private static unsafe void ApplySpotCore(SKBitmap bitmap, Spot spot)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var c = Circle.Of(spot, w, h);
        double r = c.Radius;
        // Everything the spot reads (destination with its ring, source with its ring) is copied first, so a source
        // overlapping the destination reads the pixels as they were.
        int margin = 4;
        var region = SKRectI.Union(Bounds(c.X, c.Y, r + margin, w, h), Bounds(c.SourceX, c.SourceY, r + margin, w, h));
        if (region.IsEmpty)
            return;
        var snapshot = new Snapshot(bitmap, region);
        double ox = c.SourceX - c.X, oy = c.SourceY - c.Y;

        // Heal: destination − source on the ring just outside the circle (3 radii averaged), per channel.
        var boundary = new float[BoundarySamples * 4];
        var cos = new double[BoundarySamples];
        var sin = new double[BoundarySamples];
        double ringRadius = r + 2;
        for (int k = 0; k < BoundarySamples; k++)
        {
            double a = 2 * Math.PI * k / BoundarySamples;
            cos[k] = Math.Cos(a);
            sin[k] = Math.Sin(a);
            if (spot.Mode != SpotMode.Heal)
                continue;
            for (int j = 1; j <= 3; j++)
            {
                double px = c.X + (r + j) * cos[k], py = c.Y + (r + j) * sin[k];
                var d = snapshot.Sample(px, py);
                var s = snapshot.Sample(px + ox, py + oy);
                for (int ch = 0; ch < 4; ch++)
                    boundary[k * 4 + ch] += (d[ch] - s[ch]) / 3f;
            }
        }

        var dest = SKRectI.Intersect(Bounds(c.X, c.Y, r, w, h), new SKRectI(0, 0, w, h));
        double inner = r * (1 - spot.Feather);
        byte* pixels = (byte*)bitmap.GetPixels();
        int rowBytes = bitmap.RowBytes;
        Span<float> correction = stackalloc float[4];
        for (int y = dest.Top; y < dest.Bottom; y++)
        {
            for (int x = dest.Left; x < dest.Right; x++)
            {
                double dx = x + 0.5 - c.X, dy = y + 0.5 - c.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= r)
                    continue;
                double alpha = dist <= inner ? 1 : 1 - SmoothStep(inner, r, dist);
                alpha *= spot.Opacity;
                if (alpha <= 0)
                    continue;
                var s = snapshot.Sample(x + 0.5 + ox, y + 0.5 + oy);
                correction.Clear();
                if (spot.Mode == SpotMode.Heal)
                    Membrane(boundary, cos, sin, dx, dy, dist, ringRadius, correction);
                byte* p = pixels + (long)y * rowBytes + x * 4;
                float a8 = p[3];
                for (int ch = 0; ch < 3; ch++)
                {
                    double value = p[ch] + alpha * (s[ch] + correction[ch] - p[ch]);
                    p[ch] = (byte)Math.Clamp(Math.Round(value), 0, a8);
                }
            }
        }
    }

    /// <summary>The boundary differences interpolated harmonically at (dx, dy) from the centre (Poisson kernel).</summary>
    private static void Membrane(float[] boundary, double[] cos, double[] sin, double dx, double dy, double dist, double ringRadius,
        Span<float> result)
    {
        double rho = Math.Min(dist / ringRadius, 0.97);
        double ct = dist > 1e-9 ? dx / dist : 1, st = dist > 1e-9 ? dy / dist : 0;
        double num = 1 - rho * rho, rr = 1 + rho * rho;
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, wsum = 0;
        for (int k = 0; k < cos.Length; k++)
        {
            double cosDiff = ct * cos[k] + st * sin[k];
            double wk = num / (rr - 2 * rho * cosDiff);
            s0 += wk * boundary[k * 4];
            s1 += wk * boundary[k * 4 + 1];
            s2 += wk * boundary[k * 4 + 2];
            s3 += wk * boundary[k * 4 + 3];
            wsum += wk;
        }
        result[0] = (float)(s0 / wsum);
        result[1] = (float)(s1 / wsum);
        result[2] = (float)(s2 / wsum);
        result[3] = (float)(s3 / wsum);
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / Math.Max(edge1 - edge0, 1e-9), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static SKRectI Bounds(double x, double y, double r, int w, int h) => SKRectI.Intersect(
        new SKRectI((int)Math.Floor(x - r), (int)Math.Floor(y - r), (int)Math.Ceiling(x + r) + 1, (int)Math.Ceiling(y + r) + 1),
        new SKRectI(0, 0, w, h));

    /// <summary>
    /// Part of the image (a copy, or the bitmap itself when <c>copy</c> is false) with bilinear, edge-clamped
    /// sampling (clamped to the whole image and to the part).
    /// </summary>
    private sealed unsafe class Snapshot
    {
        private readonly byte[]? _data;
        private readonly byte* _pixels;
        private readonly int _rowBytes;
        private readonly SKRectI _region;
        private readonly int _width, _height;

        public Snapshot(SKBitmap bitmap, SKRectI region, bool copy = true)
        {
            _region = region;
            _width = bitmap.Width;
            _height = bitmap.Height;
            byte* src = (byte*)bitmap.GetPixels();
            if (!copy)
            {
                _pixels = src;
                _rowBytes = bitmap.RowBytes;
                return;
            }
            _data = new byte[region.Width * region.Height * 4];
            _rowBytes = region.Width * 4;
            for (int y = 0; y < region.Height; y++)
                new ReadOnlySpan<byte>(src + (long)(region.Top + y) * bitmap.RowBytes + region.Left * 4, region.Width * 4)
                    .CopyTo(_data.AsSpan(y * _rowBytes));
        }

        /// <summary>Premultiplied RGBA (0..255) at image position (x, y) (pixel centres at +0.5).</summary>
        public float[] Sample(double x, double y)
        {
            double u = x - 0.5, v = y - 0.5;
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            float fx = (float)(u - x0), fy = (float)(v - y0);
            var result = new float[4];
            for (int ch = 0; ch < 4; ch++)
            {
                float top = At(x0, y0, ch) * (1 - fx) + At(x0 + 1, y0, ch) * fx;
                float bottom = At(x0, y0 + 1, ch) * (1 - fx) + At(x0 + 1, y0 + 1, ch) * fx;
                result[ch] = top * (1 - fy) + bottom * fy;
            }
            return result;
        }

        private float At(int x, int y, int ch)
        {
            x = Math.Clamp(Math.Clamp(x, 0, _width - 1), _region.Left, _region.Right - 1);
            y = Math.Clamp(Math.Clamp(y, 0, _height - 1), _region.Top, _region.Bottom - 1);
            if (_data is null)
                return _pixels[(long)y * _rowBytes + x * 4 + ch];
            return _data[(y - _region.Top) * _rowBytes + (x - _region.Left) * 4 + ch];
        }
    }

    // ---- Automatic source ----

    /// <summary>
    /// A good source for a spot at <paramref name="center"/> with <paramref name="radius"/> (fraction of the long
    /// side): among nearby circles fully inside the photo, the one whose surroundings look most like the spot's
    /// surroundings and whose inside is as plain as its surroundings (so no other blemish is copied). Circles
    /// overlapping other spots are avoided.
    /// </summary>
    public static BrushPoint FindSource(SKBitmap image, BrushPoint center, float radius, IEnumerable<Spot>? others = null)
    {
        using var converted = image.ColorType == SKColorType.Rgba8888 ? null : image.Copy(SKColorType.Rgba8888);
        var bitmap = converted ?? image;
        int w = bitmap.Width, h = bitmap.Height;
        double r = Math.Max(1.0, radius * Math.Max(w, h));
        double cx = center.X * w, cy = center.Y * h;
        var all = new Snapshot(bitmap, new SKRectI(0, 0, w, h), copy: false);
        var otherCircles = (others ?? []).Select(s => Circle.Of(s, w, h)).ToList();

        const int ringAngles = 48;
        var ring = Ring(all, cx, cy, r, ringAngles);
        (double X, double Y) best = (Math.Clamp(cx + 3 * r, 0, w), cy);
        double bestScore = double.MaxValue;
        foreach (double distance in new[] { 2.3, 3.2, 4.5, 6.5 })
        {
            for (int k = 0; k < 24; k++)
            {
                double a = 2 * Math.PI * k / 24;
                double sx = cx + distance * r * Math.Cos(a), sy = cy + distance * r * Math.Sin(a);
                double reach = 1.6 * r;
                bool inside = sx - reach >= 0 && sy - reach >= 0 && sx + reach <= w && sy + reach <= h;
                double score = 0;
                var candidateRing = Ring(all, sx, sy, r, ringAngles);
                for (int i = 0; i < ring.Length; i++)
                    score += (ring[i] - candidateRing[i]) * (ring[i] - candidateRing[i]);
                score /= ring.Length;
                // A plain inside: its mean and spread like its own surroundings.
                var (innerMean, innerSpread) = Inside(all, sx, sy, r);
                var (ringMean, ringSpread) = MeanAndSpread(candidateRing);
                score += 2 * (innerMean - ringMean) * (innerMean - ringMean) + Math.Max(0, innerSpread - ringSpread) * 2;
                score *= 1 + 0.02 * distance; // prefer near sources a little
                if (!inside)
                    score = score * 4 + 1e4;
                foreach (var o in otherCircles)
                {
                    double dd = Math.Sqrt((o.X - sx) * (o.X - sx) + (o.Y - sy) * (o.Y - sy));
                    if (dd < o.Radius + r)
                        score = score * 2 + 1e3;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = (sx, sy);
                }
            }
        }
        return new BrushPoint((float)Math.Clamp(best.X / w, 0, 1), (float)Math.Clamp(best.Y / h, 0, 1));
    }

    /// <summary>Luminance on two rings (1.25 r and 1.6 r) around a circle.</summary>
    private static double[] Ring(Snapshot image, double x, double y, double r, int angles)
    {
        var values = new double[angles * 2];
        for (int k = 0; k < angles; k++)
        {
            double a = 2 * Math.PI * k / angles;
            for (int j = 0; j < 2; j++)
            {
                double rr = r * (j == 0 ? 1.25 : 1.6);
                values[k * 2 + j] = Luminance(image.Sample(x + rr * Math.Cos(a), y + rr * Math.Sin(a)));
            }
        }
        return values;
    }

    /// <summary>Mean and standard deviation of the luminance inside a circle (sampled on 3 rings plus the centre).</summary>
    private static (double Mean, double Spread) Inside(Snapshot image, double x, double y, double r)
    {
        var values = new List<double> { Luminance(image.Sample(x, y)) };
        foreach (double f in new[] { 0.3, 0.6, 0.9 })
            for (int k = 0; k < 16; k++)
            {
                double a = 2 * Math.PI * (k + f) / 16;
                values.Add(Luminance(image.Sample(x + f * r * Math.Cos(a), y + f * r * Math.Sin(a))));
            }
        return MeanAndSpread(values);
    }

    private static (double Mean, double Spread) MeanAndSpread(IReadOnlyCollection<double> values)
    {
        double mean = values.Average();
        return (mean, Math.Sqrt(values.Average(v => (v - mean) * (v - mean))));
    }

    private static double Luminance(float[] p) => 0.2126 * p[0] + 0.7152 * p[1] + 0.0722 * p[2];
}
