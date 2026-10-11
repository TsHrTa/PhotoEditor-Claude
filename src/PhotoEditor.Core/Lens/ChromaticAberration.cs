using SkiaSharp;

namespace PhotoEditor.Core.Lens;

/// <summary>
/// Where a photo's red and blue images sit relative to the green one (lateral chromatic aberration): the red image
/// is scaled by <see cref="RedScale"/> around the centre, then moved by <see cref="Moves"/> (null = not moved); blue
/// likewise. The moves are what a lens whose colours are not centred on the frame (or not quite radial) adds.
/// </summary>
public sealed record ChannelShift(double RedScale, double BlueScale, ShiftField? Moves = null)
{
    public static readonly ChannelShift None = new(1, 1);

    public bool IsNone => RedScale == 1 && BlueScale == 1 && Moves is null;

    /// <summary>Scales only (no move).</summary>
    public static ChannelShift Scales(double red, double blue) => new(red, blue);
}

/// <summary>
/// Moves of the red and blue images over the photo, on a grid of <see cref="Columns"/> × <see cref="Rows"/> points
/// (point (i, j) at ((i + ½) / columns, (j + ½) / rows) of the width and height), interpolated bilinearly between
/// them and held beyond the outer ones. In half-diagonals, so the same field fits every size of the photo.
/// </summary>
public sealed class ShiftField
{
    private readonly float[] _values;

    /// <param name="values">Red x, red y, blue x, blue y per point, row by row.</param>
    public ShiftField(int columns, int rows, float[] values)
    {
        if (columns < 1 || rows < 1 || values.Length != columns * rows * 4)
            throw new ArgumentException("The values don't match the grid.", nameof(values));
        Columns = columns;
        Rows = rows;
        _values = values;
    }

    /// <summary>The same move everywhere.</summary>
    public static ShiftField Uniform(double redX, double redY, double blueX, double blueY) =>
        new(1, 1, [(float)redX, (float)redY, (float)blueX, (float)blueY]);

    public int Columns { get; }
    public int Rows { get; }

    /// <summary>The moves at (<paramref name="fx"/>, <paramref name="fy"/>), fractions of the width and height.</summary>
    public (float RedX, float RedY, float BlueX, float BlueY) At(float fx, float fy)
    {
        float u = Math.Clamp(fx * Columns - 0.5f, 0, Columns - 1), v = Math.Clamp(fy * Rows - 0.5f, 0, Rows - 1);
        int i = Math.Min((int)u, Math.Max(Columns - 2, 0)), j = Math.Min((int)v, Math.Max(Rows - 2, 0));
        float tx = u - i, ty = v - j;
        int i1 = Math.Min(i + 1, Columns - 1), j1 = Math.Min(j + 1, Rows - 1);
        float Value(int k)
        {
            float top = _values[(j * Columns + i) * 4 + k] * (1 - tx) + _values[(j * Columns + i1) * 4 + k] * tx;
            float bottom = _values[(j1 * Columns + i) * 4 + k] * (1 - tx) + _values[(j1 * Columns + i1) * 4 + k] * tx;
            return top * (1 - ty) + bottom * ty;
        }
        return (Value(0), Value(1), Value(2), Value(3));
    }
}

/// <summary>
/// Measures lateral chromatic aberration in a photo (<see cref="ChannelShift"/>). At full resolution, since the
/// shifts are fractions of a pixel even on a 24 MP photo (≈ 0.3 px on the Canon RF 24-105 F4-7.1 STM sample): in
/// patches over a grid, the sub-pixel shift at which the red (blue) fine detail correlates best with the green detail
/// is found. A scale around the centre plus a move is fitted to all patches, each weighted by how well its edges pin
/// the shift down in each direction (an edge only tells the shift across it); what the patches still show beyond
/// that (on the sample the red image is not moved radially: ≈ 0.5 px up in the top third, 0.25 px down near the
/// bottom) becomes a smooth field of moves, each grid point from its own and its neighbours' patches and pulled
/// towards the fitted model where they have little to say (sky).
/// </summary>
public static class ChromaticAberration
{
    /// <summary>Patches (and points of the move field) along each side of the photo.</summary>
    private const int Grid = 10;

    /// <summary>
    /// How strongly a grid point's move is pulled towards the fitted model, against the evidence of its patch (a
    /// patch full of well-matching edges counts 1).
    /// </summary>
    private const double Pull = 0.01;

    /// <summary>How strongly a grid point's move holds on to each neighbour's, in the same units.</summary>
    private const double Stiffness = 0.1;

    /// <summary>Largest patch side (pixels).</summary>
    private const int MaxPatch = 128;

    /// <summary>Largest shift searched for in a patch (pixels).</summary>
    private const double Reach = 5;

    /// <summary>Below this shift (pixels) at the corner and in every move, a colour counts as free of aberration.</summary>
    private const double Negligible = 0.1;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, Tuple<ChannelShift?>> Cache = new();

    /// <summary>
    /// The red and blue shifts of <paramref name="photo"/>, measured once per bitmap: <see cref="ChannelShift.None"/>
    /// when there is no aberration to speak of, null when the photo has too little detail to tell.
    /// </summary>
    public static ChannelShift? Measure(SKBitmap photo) =>
        Cache.GetValue(photo, p => Tuple.Create(MeasureCore(p))).Item1;

    private readonly record struct Patch(int Gx, int Gy, double X, double Y, double Sx, double Sy, double Wxx, double Wxy, double Wyy);

    private static ChannelShift? MeasureCore(SKBitmap photo)
    {
        using var converted = photo.ColorType == SKColorType.Rgba8888 ? null : photo.Copy(SKColorType.Rgba8888);
        var source = converted ?? photo;
        int w = source.Width, h = source.Height;
        int size = Math.Min(MaxPatch, Math.Min(w, h) / Grid);
        if (size < 32)
            return null;
        int margin = (int)Math.Ceiling(Reach) + 3;
        var red = new List<Patch>();
        var blue = new List<Patch>();
        var gate = new object();
        var pixels = source.GetPixels();
        int rowBytes = source.RowBytes;
        Parallel.For(0, Grid * Grid, index =>
        {
            int gx = index % Grid, gy = index / Grid;
            int x0 = Math.Clamp((int)((gx + 0.5) * w / Grid) - size / 2, margin, w - margin - size);
            int y0 = Math.Clamp((int)((gy + 0.5) * h / Grid) - size / 2, margin, h - margin - size);
            if (x0 < margin || y0 < margin)
                return;
            int n = size + 2 * margin;
            var planes = new float[3][];
            for (int c = 0; c < 3; c++)
                planes[c] = new float[n * n];
            unsafe
            {
                for (int y = 0; y < n; y++)
                {
                    byte* row = (byte*)pixels + (long)(y0 - margin + y) * rowBytes + (x0 - margin) * 4;
                    for (int x = 0; x < n; x++)
                        for (int c = 0; c < 3; c++)
                            planes[c][y * n + x] = row[x * 4 + c] / 255f;
                }
            }
            for (int c = 0; c < 3; c++)
                planes[c] = HighPass(planes[c], n);
            var g = planes[1];
            // The structure tensor of the green detail: how well the patch pins a shift down in x and in y.
            double txx = 0, txy = 0, tyy = 0;
            for (int y = margin; y < margin + size; y++)
                for (int x = margin; x < margin + size; x++)
                {
                    double dx = (g[y * n + x + 1] - g[y * n + x - 1]) / 2, dy = (g[(y + 1) * n + x] - g[(y - 1) * n + x]) / 2;
                    txx += dx * dx;
                    txy += dx * dy;
                    tyy += dy * dy;
                }
            double energy = (txx + tyy) / (size * size);
            // Flat patches (sky, blur) say nothing: needs edges of a few percent contrast.
            if (energy < 3e-6)
                return;
            double cx = x0 + size / 2.0, cy = y0 + size / 2.0;
            foreach (var (plane, list) in new[] { (planes[0], red), (planes[2], blue) })
            {
                var (sx, sy, score) = BestShift(plane, g, n, margin, size);
                if (score < 0.6)
                    continue;
                // Weight: the tensor normalised (direction), times the evidence (how much edge, how well it matches).
                double trace = txx + tyy, weight = score * score * Math.Min(1, energy / 5e-5);
                lock (gate)
                    list.Add(new Patch(gx, gy, cx, cy, sx, sy, txx / trace * weight, txy / trace * weight, tyy / trace * weight));
            }
        });
        double halfDiagonal = Math.Sqrt((double)w * w + (double)h * h) / 2;
        if (Fit(red, w / 2.0, h / 2.0) is not { } r || Fit(blue, w / 2.0, h / 2.0) is not { } b)
            return null;
        var redMoves = Field(r.Model, red, w, h);
        var blueMoves = Field(b.Model, blue, w, h);
        bool redSmall = Math.Abs(r.Model.A) * halfDiagonal < Negligible && redMoves.All(m => Math.Sqrt(m.X * m.X + m.Y * m.Y) < Negligible);
        bool blueSmall = Math.Abs(b.Model.A) * halfDiagonal < Negligible && blueMoves.All(m => Math.Sqrt(m.X * m.X + m.Y * m.Y) < Negligible);
        if (redSmall && blueSmall)
            return ChannelShift.None;
        var values = new float[Grid * Grid * 4];
        for (int i = 0; i < Grid * Grid; i++)
        {
            if (!redSmall)
                (values[i * 4], values[i * 4 + 1]) = ((float)(redMoves[i].X / halfDiagonal), (float)(redMoves[i].Y / halfDiagonal));
            if (!blueSmall)
                (values[i * 4 + 2], values[i * 4 + 3]) = ((float)(blueMoves[i].X / halfDiagonal), (float)(blueMoves[i].Y / halfDiagonal));
        }
        return new ChannelShift(redSmall ? 1 : 1 + r.Model.A, blueSmall ? 1 : 1 + b.Model.A, new ShiftField(Grid, Grid, values));
    }

    /// <summary>
    /// The move (pixels, after the model's scale) at each grid point: the model's move plus what the point's patch
    /// shows beyond the model, as a membrane: each point also holds on to its 4 neighbours (<see cref="Stiffness"/>),
    /// so points whose patch has little to say (sky, dark ground) follow their neighbours rather than the model, and
    /// all are pulled slightly towards the model (<see cref="Pull"/>). Patches far off what their neighbours show (other
    /// colours at the edge, a moving subject) are dropped and the field made again.
    /// </summary>
    private static (double X, double Y)[] Field((double A, double Tx, double Ty) model, List<Patch> patches, int w, int h)
    {
        // What each patch shows beyond the model.
        var beyond = patches.Select(p => (P: p,
            Ex: p.Sx - (model.A * (p.X - w / 2.0) + model.Tx), Ey: p.Sy - (model.A * (p.Y - h / 2.0) + model.Ty))).ToList();
        var residual = new (double X, double Y)[Grid * Grid];
        for (int round = 0; round < 2; round++)
        {
            // Data term per point: the patch's tensor A and A × its shift.
            var data = new (double Axx, double Axy, double Ayy, double Vx, double Vy)[Grid * Grid];
            foreach (var (p, ex, ey) in beyond)
                data[p.Gy * Grid + p.Gx] = (p.Wxx, p.Wxy, p.Wyy, p.Wxx * ex + p.Wxy * ey, p.Wxy * ex + p.Wyy * ey);
            Array.Clear(residual);
            for (int sweep = 0; sweep < 200; sweep++)
                for (int j = 0; j < Grid; j++)
                    for (int i = 0; i < Grid; i++)
                    {
                        var d = data[j * Grid + i];
                        double sx = 0, sy = 0;
                        int count = 0;
                        foreach (var (ni, nj) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                        {
                            if (ni < 0 || nj < 0 || ni >= Grid || nj >= Grid)
                                continue;
                            sx += residual[nj * Grid + ni].X;
                            sy += residual[nj * Grid + ni].Y;
                            count++;
                        }
                        double diagonal = Stiffness * count + Pull;
                        double axx = d.Axx + diagonal, axy = d.Axy, ayy = d.Ayy + diagonal;
                        double vx = d.Vx + Stiffness * sx, vy = d.Vy + Stiffness * sy;
                        double det = axx * ayy - axy * axy;
                        residual[j * Grid + i] = ((ayy * vx - axy * vy) / det, (axx * vy - axy * vx) / det);
                    }
            if (round == 1 || beyond.Count == 0)
                break;
            // Each patch's distance from what its neighbours show, along the directions it pins down.
            var off = beyond.Select(b =>
            {
                double fx = 0, fy = 0;
                int count = 0;
                foreach (var (ni, nj) in new[] { (b.P.Gx - 1, b.P.Gy), (b.P.Gx + 1, b.P.Gy), (b.P.Gx, b.P.Gy - 1), (b.P.Gx, b.P.Gy + 1) })
                {
                    if (ni < 0 || nj < 0 || ni >= Grid || nj >= Grid)
                        continue;
                    fx += residual[nj * Grid + ni].X;
                    fy += residual[nj * Grid + ni].Y;
                    count++;
                }
                double ex = b.Ex - fx / count, ey = b.Ey - fy / count, weight = b.P.Wxx + b.P.Wyy;
                return weight > 0 ? Math.Sqrt((b.P.Wxx * ex * ex + 2 * b.P.Wxy * ex * ey + b.P.Wyy * ey * ey) / weight) : 0;
            }).ToArray();
            double limit = Math.Max(3 * off.OrderBy(e => e).ElementAt(off.Length / 2), 0.15);
            beyond = beyond.Where((_, i) => off[i] <= limit).ToList();
        }
        return residual.Select(r => (model.Tx + r.X, model.Ty + r.Y)).ToArray();
    }

    /// <summary>
    /// The shift model s(p) = a (p − centre) + t (pixels) that best fits the patches' shifts, by weighted least
    /// squares; patches far off the fit (other colours at the edge, a moving subject) are dropped and the fit repeated.
    /// Null with too few patches.
    /// </summary>
    private static ((double A, double Tx, double Ty) Model, List<Patch> Used)? Fit(List<Patch> patches, double cx, double cy)
    {
        var used = patches.ToList();
        (double A, double Tx, double Ty)? model = null;
        for (int round = 0; round < 3; round++)
        {
            if (used.Count < 12)
                return null;
            // Normal equations of Σ (s − M θ)ᵀ W (s − M θ), M = [[dx, 1, 0], [dy, 0, 1]], θ = (a, tx, ty).
            var n = new double[3, 3];
            var v = new double[3];
            foreach (var p in used)
            {
                double dx = p.X - cx, dy = p.Y - cy;
                // W M (2 × 3) and W s
                double r0a = p.Wxx * dx + p.Wxy * dy, r0x = p.Wxx, r0y = p.Wxy;
                double r1a = p.Wxy * dx + p.Wyy * dy, r1x = p.Wxy, r1y = p.Wyy;
                double ws0 = p.Wxx * p.Sx + p.Wxy * p.Sy, ws1 = p.Wxy * p.Sx + p.Wyy * p.Sy;
                n[0, 0] += dx * r0a + dy * r1a; n[0, 1] += dx * r0x + dy * r1x; n[0, 2] += dx * r0y + dy * r1y;
                n[1, 1] += r0x; n[1, 2] += r0y; n[2, 2] += r1y;
                v[0] += dx * ws0 + dy * ws1; v[1] += ws0; v[2] += ws1;
            }
            n[1, 0] = n[0, 1]; n[2, 0] = n[0, 2]; n[2, 1] = n[1, 2];
            if (Solve(n, v) is not { } theta)
                return null;
            model = (theta[0], theta[1], theta[2]);
            // Residual of each patch along the directions it pins down.
            var residuals = used.Select(p =>
            {
                double ex = p.Sx - (theta[0] * (p.X - cx) + theta[1]), ey = p.Sy - (theta[0] * (p.Y - cy) + theta[2]);
                double weight = p.Wxx + p.Wyy;
                return weight > 0 ? Math.Sqrt((p.Wxx * ex * ex + 2 * p.Wxy * ex * ey + p.Wyy * ey * ey) / weight) : 0;
            }).ToArray();
            double median = residuals.OrderBy(e => e).ElementAt(residuals.Length / 2);
            double limit = Math.Max(3 * median, 0.15);
            var kept = used.Where((_, i) => residuals[i] <= limit).ToList();
            if (kept.Count == used.Count)
                break;
            used = kept;
        }
        return model is { } m ? (m, used) : null;
    }

    /// <summary>Solves the 3 × 3 system (Cramer's rule); null when singular.</summary>
    private static double[]? Solve(double[,] m, double[] v)
    {
        double Det(double[,] a) =>
            a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0])
            + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        double d = Det(m);
        if (Math.Abs(d) < 1e-12 * Math.Max(1, Math.Abs(m[0, 0] * m[1, 1] * m[2, 2])))
            return null;
        var result = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var a = (double[,])m.Clone();
            for (int i = 0; i < 3; i++)
                a[i, k] = v[i];
            result[k] = Det(a) / d;
        }
        return result;
    }

    /// <summary>
    /// The shift (sx, sy) at which <paramref name="channel"/>(p + s) best matches <paramref name="green"/>(p) over the
    /// patch (normalised correlation), and that correlation: whole-pixel steps over ±<see cref="Reach"/> on every
    /// second pixel, quarter-pixel steps around the best, then a parabola through the neighbours per axis.
    /// </summary>
    private static (double X, double Y, double Score) BestShift(float[] channel, float[] green, int n, int margin, int size)
    {
        int reach = (int)Reach;
        double bx = 0, by = 0, best = double.MinValue;
        for (int sy = -reach; sy <= reach; sy++)
            for (int sx = -reach; sx <= reach; sx++)
            {
                double s = Correlation(channel, green, n, margin, size, sx, sy, 2);
                if (s > best)
                    (best, bx, by) = (s, sx, sy);
            }
        const double step = 0.25;
        const int k = 2; // ±0.5 px: the whole-pixel best is within half a pixel
        var scores = new double[2 * k + 1, 2 * k + 1];
        int bi = k, bj = k;
        best = double.MinValue;
        for (int j = -k; j <= k; j++)
            for (int i = -k; i <= k; i++)
            {
                double s = Correlation(channel, green, n, margin, size, bx + i * step, by + j * step, 1);
                scores[j + k, i + k] = s;
                if (s > best)
                    (best, bi, bj) = (s, i + k, j + k);
            }
        static double Vertex(double a, double b, double c)
        {
            double d = a - 2 * b + c;
            return d < 0 ? Math.Clamp(0.5 * (a - c) / d, -0.5, 0.5) : 0;
        }
        double ox = bi > 0 && bi < 2 * k ? Vertex(scores[bj, bi - 1], scores[bj, bi], scores[bj, bi + 1]) : 0;
        double oy = bj > 0 && bj < 2 * k ? Vertex(scores[bj - 1, bi], scores[bj, bi], scores[bj + 1, bi]) : 0;
        return (bx + (bi - k + ox) * step, by + (bj - k + oy) * step, best);
    }

    /// <summary>
    /// Normalised correlation over the patch of <paramref name="channel"/> moved by half of (sx, sy) and
    /// <paramref name="green"/> moved back by the other half: both interpolated (smoothed) alike, as interpolating only
    /// one side biases the best match towards whole pixels. A move of the whole patch has the same bilinear weights at
    /// every sample.
    /// </summary>
    private static double Correlation(float[] channel, float[] green, int n, int margin, int size, double sx, double sy, int stride)
    {
        var c = new Shifted(sx / 2, sy / 2, n);
        var g = new Shifted(-sx / 2, -sy / 2, n);
        double cg = 0, cc = 0, gg = 0;
        for (int y = margin; y < margin + size; y += stride)
        {
            float rowCg = 0, rowCc = 0, rowGg = 0;
            for (int x = margin; x < margin + size; x += stride)
            {
                int i = y * n + x;
                float cv = c.At(channel, i), gv = g.At(green, i);
                rowCg += cv * gv;
                rowCc += cv * cv;
                rowGg += gv * gv;
            }
            cg += rowCg;
            cc += rowCc;
            gg += rowGg;
        }
        return cc > 0 && gg > 0 ? cg / Math.Sqrt(cc * gg) : 0;
    }

    /// <summary>Bilinear sampling of an n × n plane moved by a constant (dx, dy): the same 4 weights everywhere.</summary>
    private readonly struct Shifted
    {
        private readonly int _offset, _n;
        private readonly float _w00, _w10, _w01, _w11;

        public Shifted(double dx, double dy, int n)
        {
            int ix = (int)Math.Floor(dx), iy = (int)Math.Floor(dy);
            float fx = (float)(dx - ix), fy = (float)(dy - iy);
            _offset = iy * n + ix;
            _n = n;
            _w00 = (1 - fx) * (1 - fy);
            _w10 = fx * (1 - fy);
            _w01 = (1 - fx) * fy;
            _w11 = fx * fy;
        }

        /// <summary>The value at sample <paramref name="i"/> plus the move.</summary>
        public float At(float[] v, int i)
        {
            int j = i + _offset;
            return _w00 * v[j] + _w10 * v[j + 1] + _w01 * v[j + _n] + _w11 * v[j + _n + 1];
        }
    }

    /// <summary>
    /// The detail between about 2 and 5 pixels: smoothed by a 5-tap binomial (σ = 1), minus its 5 × 5 average (so
    /// different channel levels don't matter). Without the smoothing, the finest detail, which bilinear interpolation
    /// cannot move by a fraction of a pixel faithfully, pulls the best match towards whole or half pixels.
    /// Valid from 4 samples in from the border.
    /// </summary>
    private static float[] HighPass(float[] values, int n)
    {
        var across = new float[values.Length];
        var smooth = new float[values.Length];
        for (int y = 0; y < n; y++)
            for (int x = 2; x < n - 2; x++)
            {
                int i = y * n + x;
                across[i] = (values[i - 2] + 4 * values[i - 1] + 6 * values[i] + 4 * values[i + 1] + values[i + 2]) / 16f;
            }
        for (int y = 2; y < n - 2; y++)
            for (int x = 2; x < n - 2; x++)
            {
                int i = y * n + x;
                smooth[i] = (across[i - 2 * n] + 4 * across[i - n] + 6 * across[i] + 4 * across[i + n] + across[i + 2 * n]) / 16f;
            }
        var result = new float[values.Length];
        for (int y = 4; y < n - 4; y++)
            for (int x = 4; x < n - 4; x++)
            {
                float sum = 0;
                for (int j = -2; j <= 2; j++)
                    for (int i = -2; i <= 2; i++)
                        sum += smooth[(y + j) * n + x + i];
                result[y * n + x] = smooth[y * n + x] - sum / 25f;
            }
        return result;
    }
}
