using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Core.Lens;

/// <summary>Which corrections Upright makes (like Lightroom's buttons).</summary>
public enum UprightMode
{
    /// <summary>Level plus vertical perspective, with a milder horizontal perspective: a balanced result.</summary>
    Auto,

    /// <summary>Rotation only: horizontal and vertical lines level / upright.</summary>
    Level,

    /// <summary>Rotation and vertical perspective: converging verticals made parallel.</summary>
    Vertical,

    /// <summary>Rotation, vertical and horizontal perspective.</summary>
    Full,
}

/// <summary>A straight edge found in a photo, in its pixels.</summary>
public readonly record struct LineSegment(double X1, double Y1, double X2, double Y2)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}

/// <summary>
/// Upright: finds the straight edges of a photo (<see cref="Detect"/>) and the Transform (vertical, horizontal,
/// rotate) that makes the near-vertical ones vertical and the near-horizontal ones level (<see cref="Solve"/>).
/// Works on the photo after its lens corrections (distortion bends straight lines), before the Transform.
/// </summary>
public static class Upright
{
    /// <summary>Long side of the copy the edges are found in.</summary>
    public const int AnalysisSize = 900;

    /// <summary>The Transform values for <paramref name="photo"/> (lens-corrected, not transformed); null when it has too few straight edges.</summary>
    public static (double Vertical, double Horizontal, double Rotate)? Estimate(SKBitmap photo, UprightMode mode)
    {
        double scale = Math.Min(1, AnalysisSize / (double)Math.Max(photo.Width, photo.Height));
        int w = Math.Max(8, (int)Math.Round(photo.Width * scale)), h = Math.Max(8, (int)Math.Round(photo.Height * scale));
        using var small = photo.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Could not scale the photo.");
        var segments = Detect(small);
        return Solve(segments, w, h, mode);
    }

    // ---- Finding straight edges (a small version of the LSD line segment detector) ----

    /// <summary>Angle tolerance of the pixels of one edge.</summary>
    private const double Tolerance = 22.5 * Math.PI / 180;

    /// <summary>Straight edges of <paramref name="bitmap"/> at least 4 % of its long side long.</summary>
    public static List<LineSegment> Detect(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var grey = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bitmap.GetPixel(x, y);
                grey[y * w + x] = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
            }
        }
        // Gradient on 2 × 2 blocks (at pixel corners), level-line angle along the edge.
        var magnitude = new float[w * h];
        var angle = new float[w * h];
        for (int y = 0; y < h - 1; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                int i = y * w + x;
                float a = grey[i], b = grey[i + 1], c = grey[i + w], d = grey[i + w + 1];
                float gx = (b + d - a - c) / 2, gy = (c + d - a - b) / 2;
                magnitude[i] = MathF.Sqrt(gx * gx + gy * gy);
                angle[i] = MathF.Atan2(gx, -gy);
            }
        }
        // Weak gradients are noise (LSD: 2 grey levels of quantisation over sin 22.5°).
        const float threshold = 5.2f;
        var order = Enumerable.Range(0, w * h).Where(i => magnitude[i] > threshold)
            .OrderByDescending(i => magnitude[i]).ToArray();
        var used = new bool[w * h];
        var segments = new List<LineSegment>();
        double minLength = 0.04 * Math.Max(w, h);
        var region = new List<int>();
        var stack = new Stack<int>();
        foreach (int seed in order)
        {
            if (used[seed])
                continue;
            // Grow a region of neighbouring pixels whose edge runs the same way.
            region.Clear();
            used[seed] = true;
            region.Add(seed);
            stack.Push(seed);
            double sumCos = Math.Cos(angle[seed]), sumSin = Math.Sin(angle[seed]);
            double regionAngle = angle[seed];
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w - 1 || ny >= h - 1)
                            continue;
                        int n = ny * w + nx;
                        if (used[n] || magnitude[n] <= threshold || AngleDiff(angle[n], regionAngle) > Tolerance)
                            continue;
                        used[n] = true;
                        region.Add(n);
                        stack.Push(n);
                        sumCos += Math.Cos(angle[n]);
                        sumSin += Math.Sin(angle[n]);
                        regionAngle = Math.Atan2(sumSin, sumCos);
                    }
                }
            }
            if (region.Count < minLength)
                continue;
            if (Fit(region, w, magnitude) is { } segment && segment.Length >= minLength)
                segments.Add(segment);
        }
        return segments;
    }

    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % (2 * Math.PI);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }

    /// <summary>The segment along a region's main axis; null when the region is not thin (not a straight edge).</summary>
    private static LineSegment? Fit(List<int> region, int w, float[] magnitude)
    {
        double sw = 0, cx = 0, cy = 0;
        foreach (int p in region)
        {
            double m = magnitude[p];
            sw += m;
            cx += m * (p % w + 1.0);
            cy += m * (p / w + 1.0);
        }
        cx /= sw;
        cy /= sw;
        double ixx = 0, iyy = 0, ixy = 0;
        foreach (int p in region)
        {
            double m = magnitude[p], dx = p % w + 1.0 - cx, dy = p / w + 1.0 - cy;
            ixx += m * dx * dx;
            iyy += m * dy * dy;
            ixy += m * dx * dy;
        }
        // Main axis: the eigenvector of the larger eigenvalue.
        double theta = 0.5 * Math.Atan2(2 * ixy, ixx - iyy);
        double ux = Math.Cos(theta), uy = Math.Sin(theta);
        double lo = double.MaxValue, hi = double.MinValue, wlo = double.MaxValue, whi = double.MinValue;
        foreach (int p in region)
        {
            double dx = p % w + 1.0 - cx, dy = p / w + 1.0 - cy;
            double along = dx * ux + dy * uy, across = -dx * uy + dy * ux;
            lo = Math.Min(lo, along);
            hi = Math.Max(hi, along);
            wlo = Math.Min(wlo, across);
            whi = Math.Max(whi, across);
        }
        double length = hi - lo, width = whi - wlo;
        // A straight edge: long and thin, and the pixels fill the rectangle reasonably (not a blob or a curve).
        if (width > Math.Max(3, length / 12) || region.Count < 0.5 * length)
            return null;
        return new LineSegment(cx + ux * lo, cy + uy * lo, cx + ux * hi, cy + uy * hi);
    }

    // ---- Solving for the Transform ----

    /// <summary>Lines within this of vertical / horizontal count as meant to be vertical / horizontal.</summary>
    private const double ClassifyDegrees = 25;

    /// <summary>
    /// Cost of a correction, as a fraction of all lines' weight per full slider (±100; rotate ±10°): a correction must
    /// straighten clearly more lines than this to be made, so one long edge (a roof, a tree) can't decide alone.
    /// </summary>
    private const double PenaltyPerspective = 0.15, PenaltyRotate = 0.05;

    /// <summary>
    /// The Transform values (for a <paramref name="width"/> × <paramref name="height"/> photo) that make as many of
    /// its near-vertical lines vertical and near-horizontal lines level as <paramref name="mode"/> allows; null with
    /// too few lines. Lines count by their length up to 3 × the shortest found (many agreeing lines beat one long one).
    /// </summary>
    public static (double Vertical, double Horizontal, double Rotate)? Solve(IReadOnlyList<LineSegment> segments, int width, int height, UprightMode mode)
    {
        double cx = width / 2.0, cy = height / 2.0;
        double longSide = Math.Max(width, height), minLength = 0.04 * longSide;
        var lines = segments.Select(s => (X1: s.X1 - cx, Y1: s.Y1 - cy, X2: s.X2 - cx, Y2: s.Y2 - cy,
            Weight: Math.Min(s.Length, 3 * minLength) / minLength)).ToArray();
        static double Tilt(double x1, double y1, double x2, double y2) => Math.Atan2(x2 - x1, y2 - y1) * 180 / Math.PI; // 0 = vertical
        var verticals = lines.Where(l => Math.Abs(Fold(Tilt(l.X1, l.Y1, l.X2, l.Y2))) < ClassifyDegrees).ToArray();
        var horizontals = lines.Where(l => Math.Abs(Fold(Tilt(l.X1, l.Y1, l.X2, l.Y2) - 90)) < ClassifyDegrees).ToArray();
        if (verticals.Length + horizontals.Length < 4)
            return null;
        bool useHorizontals = mode is UprightMode.Full or UprightMode.Auto or UprightMode.Level;
        double total = verticals.Sum(l => l.Weight) + (useHorizontals ? horizontals.Sum(l => l.Weight) : 0);
        if (total <= 0)
            return null;
        // Auto is Full made gentler: horizontal perspective costs more.
        double penaltyH = mode == UprightMode.Auto ? 2 * PenaltyPerspective : PenaltyPerspective;

        // How many lines (by weight) are within `tolerance` degrees of vertical / level after the correction.
        double Score(double v, double hz, double r, double tolerance)
        {
            var p = Perspective.For(new AdjustmentSettings { TransformVertical = v, TransformHorizontal = hz, TransformRotate = r }, width, height);
            var forward = p?.Full.Inverse() ?? Matrix3.Identity;
            double Support(double error) => error >= tolerance ? 0 : 1 - error * error / (tolerance * tolerance);
            double score = 0;
            foreach (var l in verticals)
            {
                var (ax, ay) = forward.Apply(l.X1, l.Y1);
                var (bx, by) = forward.Apply(l.X2, l.Y2);
                score += l.Weight * Support(Math.Abs(Fold(Tilt(ax, ay, bx, by))));
            }
            if (useHorizontals)
            {
                foreach (var l in horizontals)
                {
                    var (ax, ay) = forward.Apply(l.X1, l.Y1);
                    var (bx, by) = forward.Apply(l.X2, l.Y2);
                    score += l.Weight * Support(Math.Abs(Fold(Tilt(ax, ay, bx, by) - 90)));
                }
            }
            return score - total * (PenaltyPerspective * Math.Abs(v) / 100 + penaltyH * Math.Abs(hz) / 100 + PenaltyRotate * Math.Abs(r) / 10);
        }

        static bool Varies(UprightMode m, int what) => what switch
        {
            0 => m is UprightMode.Vertical or UprightMode.Full or UprightMode.Auto, // vertical
            1 => m is UprightMode.Full or UprightMode.Auto, // horizontal
            _ => true, // rotate
        };
        // Coarse grid with a wide tolerance, then finer grids around the best with a narrower one.
        double bestV = 0, bestH = 0, bestR = 0;
        double stepV = 10, stepH = 10, stepR = 1;
        double spanV = 100, spanH = 100, spanR = 10;
        double[] tolerances = [4, 2, 1, 1, 1];
        foreach (double tolerance in tolerances)
        {
            double cV = bestV, cH = bestH, cR = bestR;
            double best = Score(cV, cH, cR, tolerance);
            for (double v = Varies(mode, 0) ? cV - spanV : 0; v <= (Varies(mode, 0) ? cV + spanV : 0) + 1e-9; v += stepV)
            {
                if (Math.Abs(v) > 100)
                    continue;
                for (double hz = Varies(mode, 1) ? cH - spanH : 0; hz <= (Varies(mode, 1) ? cH + spanH : 0) + 1e-9; hz += stepH)
                {
                    if (Math.Abs(hz) > 100)
                        continue;
                    for (double r = cR - spanR; r <= cR + spanR + 1e-9; r += stepR)
                    {
                        if (Math.Abs(r) > 10)
                            continue;
                        double score = Score(v, hz, r, tolerance);
                        if (score > best)
                            (best, bestV, bestH, bestR) = (score, v, hz, r);
                    }
                }
            }
            (spanV, spanH, spanR) = (stepV, stepH, stepR);
            (stepV, stepH, stepR) = (stepV / 4, stepH / 4, stepR / 4);
        }
        return (Math.Round(bestV) + 0.0, Math.Round(bestH) + 0.0, Math.Round(bestR, 1) + 0.0);
    }

    /// <summary>An angle in degrees folded into −90..90 (a line has no direction).</summary>
    private static double Fold(double degrees)
    {
        double d = degrees % 180;
        if (d > 90)
            d -= 180;
        if (d < -90)
            d += 180;
        return d;
    }
}
