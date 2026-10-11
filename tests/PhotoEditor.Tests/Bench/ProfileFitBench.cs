using System.Globalization;
using ImageMagick;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// Fits Lightroom's default rendering of a RAW: our scene-linear decode is aligned tile by tile to a Lightroom 16-bit
/// Adobe RGB TIFF of the same file (Lightroom corrects lens distortion and vignetting, so each tile gets its own shift),
/// then prints the tone curve (scene luminance → Lightroom luminance, per 1/4 stop) and the colour difference per
/// OKLCh hue (hue shift, chroma ratio at the same lightness). Runs only when PHOTOEDITOR_BENCH and PHOTOEDITOR_LR_DIR are set.
/// </summary>
public sealed class ProfileFitBench(ITestOutputHelper output)
{
    private const int Tile = 256, Search = 72;

    [Fact]
    public void Fit()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        if (raw is null || dir is null)
            return;
        var lrFile = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_FILE") ?? "IMG_5645 sharpening 0";
        var (lr, w, h) = LoadAdobeRgb(Path.Combine(dir, lrFile + ".tif"));
        var (scene, sw, sh) = RawImageLoader.LoadScene(raw, RawImageLoader.DefaultDemosaic);
        Assert.Equal((w, h), (sw, sh));
        var lrY = Luminance(lr);
        var sY = Luminance(scene);
        var lrLog = lrY.Select(v => MathF.Log2(v + 1e-4f)).ToArray();
        var sLog = sY.Select(v => MathF.Log2(v + 1e-4f)).ToArray();

        // pairs: (scene rgb, lr rgb, radius) from 2 x 2 averages of aligned tiles
        var pairs = new List<(float[] S, float[] L, float Radius)>();
        var shifts = new List<string>();
        float cx = w / 2f, cy = h / 2f, half = MathF.Sqrt(cx * cx + cy * cy);
        for (int ty = Search; ty + Tile + Search <= h; ty += Tile)
            for (int tx = Search; tx + Tile + Search <= w; tx += Tile)
            {
                var (dx, dy, score) = Align(lrLog, sLog, w, tx, ty);
                if (score < 0.8)
                    continue;
                shifts.Add($"({tx},{ty}):{dx},{dy}");
                for (int y = 0; y + Block <= Tile; y += Block)
                    for (int x = 0; x + Block <= Tile; x += Block)
                    {
                        var s = new float[3];
                        var l = new float[3];
                        for (int c = 0; c < 3; c++)
                        {
                            l[c] = Avg(lr, w, tx + x, ty + y, c);
                            s[c] = Avg(scene, w, tx + x + dx, ty + y + dy, c);
                        }
                        float px = tx + x - cx, py = ty + y - cy;
                        pairs.Add((s, l, MathF.Sqrt(px * px + py * py) / half));
                    }
            }
        output.WriteLine($"{pairs.Count} pairs from {shifts.Count} tiles; shifts {string.Join(" ", shifts.Take(12))} …");

        // vignetting: Lightroom / ours luminance ratio by radius, on mid-grey-ish pixels (compares like with like)
        output.WriteLine("radius band: median log2(LR Y / scene Y) for scene Y in [0.02, 0.08]");
        for (int band = 0; band < 10; band++)
        {
            var r = pairs.Where(p => p.Radius >= band / 10f && p.Radius < (band + 1) / 10f)
                .Select(p => (S: Y(p.S), L: Y(p.L))).Where(p => p.S is > 0.02f and < 0.08f && p.L > 0)
                .Select(p => MathF.Log2(p.L / p.S)).ToList();
            if (r.Count > 100)
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {band / 10f:F1}: {Median(r):F2} ({r.Count})"));
        }

        var central = pairs.Where(p => p.Radius < 0.4f).ToList();
        output.WriteLine("tone: scene log2 Y → LR Y (linear), LR sRGB-encoded ×255, p25 / p75 encoded; central pixels");
        for (float e = -12; e <= 1.5f; e += 0.25f)
        {
            var bin = central.Where(p => Y(p.S) is var y && y > 0 && MathF.Log2(y) >= e && MathF.Log2(y) < e + 0.25f)
                .Select(p => Y(p.L)).ToList();
            if (bin.Count < 50)
                continue;
            bin.Sort();
            float med = bin[bin.Count / 2];
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {e + 0.125f,6:F2} ({MathF.Pow(2, e + 0.125f):F5}): {med:F5}  enc {Enc(med):F1}  [{Enc(bin[bin.Count / 4]):F1}, {Enc(bin[bin.Count * 3 / 4]):F1}] n={bin.Count}"));
        }

        bool colourOnly = Environment.GetEnvironmentVariable("PHOTOEDITOR_COLOUR_ONLY") is not null;
        // fit (exposure, white scene, toe, contrast power) of the curve form to the centre (no vignetting difference)
        var data = new List<(float X, float Y, int N)>();
        var inner = pairs.Where(p => p.Radius < 0.3f).ToList();
        for (float e = -11; e <= 1.5f; e += 0.25f)
        {
            var bin = inner.Where(p => Y(p.S) is var y && y > 0 && MathF.Log2(y) >= e && MathF.Log2(y) < e + 0.25f)
                .Select(p => Y(p.L)).ToList();
            if (bin.Count >= 50)
                data.Add((MathF.Pow(2, e + 0.125f), Median(bin), bin.Count));
        }
        double Error(double[] q)
        {
            if (q[0] <= 0.1 || q[1] <= 0.3 || q[2] < 0 || q[3] < 0.5 || q[3] > 3) return 1e9;
            double sum = 0;
            foreach (var (x, y, _) in data)
            {
                double d = Enc((float)CurveForm(x, q)) - Enc(y);
                sum += d * d;
            }
            return sum / data.Count;
        }
        var fit = colourOnly ? [4.4, 1.6, 0.012, 1.0] : NelderMead(Error, [4.4, 1.6, 0.012, 1.0]);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"fit: exposure {fit[0]:F4} white {fit[1]:F4} toe {fit[2]:F5} power {fit[3]:F4}; rms {Math.Sqrt(Error(fit)):F2} levels (old curve {Math.Sqrt(Error([4.4, 1.6, 0.012, 1.0])):F2})"));
        foreach (var (x, y, n) in data)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {MathF.Log2(x),6:F2}: LR {Enc(y),5:F1} fit {Enc((float)CurveForm(x, fit)),5:F1} old {Enc((float)CurveForm(x, [4.4, 1.6, 0.012, 1.0])),5:F1} n={n}"));

        // the same fit through the hue-preserving rendering (curve on the brightest and darkest channel) per pixel
        var rng = new Random(1);
        var sample = inner.Where(p => Y(p.S) > 0).OrderBy(_ => rng.Next()).Take(40000)
            .Select(p => (S: p.S, Bin: (int)MathF.Floor((MathF.Log2(Y(p.S)) + 11) * 4), L: Enc(Y(p.L)))).Where(p => p.Bin >= 0).ToArray();
        var targets = sample.GroupBy(p => p.Bin).Where(g => g.Count() >= 30)
            .ToDictionary(g => g.Key, g => Median(g.Select(p => p.L).ToList()));
        double RenderedError(double[] q)
        {
            if (q[0] <= 0.1 || q[1] <= 0.3 || q[2] < 0 || q[3] < 0.5 || q[3] > 3) return 1e9;
            var model = new Dictionary<int, List<float>>();
            foreach (var p in sample)
            {
                if (!targets.ContainsKey(p.Bin)) continue;
                float hi = MathF.Max(p.S[0], MathF.Max(p.S[1], p.S[2])), lo = MathF.Min(p.S[0], MathF.Min(p.S[1], p.S[2]));
                float h = (float)CurveForm(hi, q), l = (float)CurveForm(lo, q);
                float k = hi > lo ? (h - l) / (hi - lo) : 0;
                float y = Y([l + (p.S[0] - lo) * k, l + (p.S[1] - lo) * k, l + (p.S[2] - lo) * k]);
                if (!model.TryGetValue(p.Bin, out var list)) model[p.Bin] = list = [];
                list.Add(Enc(y));
            }
            double sum = 0;
            foreach (var (bin, list) in model)
            {
                double d = Median(list) - targets[bin];
                sum += d * d;
            }
            return sum / model.Count;
        }
        var rendered = colourOnly ? fit : NelderMead(RenderedError, fit);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"fit through the rendering: exposure {rendered[0]:F4} white {rendered[1]:F4} toe {rendered[2]:F5} power {rendered[3]:F4}; rms {Math.Sqrt(RenderedError(rendered)):F2} (luminance fit gives {Math.Sqrt(RenderedError(fit)):F2})"));

        output.WriteLine("colour per OKLCh hue of ours (chroma > 0.04): median hue shift (deg), chroma ratio LR / ours, ours L, LR L");
        var colour = central.Select(p =>
        {
            float r0 = p.S[0], g0 = p.S[1], b0 = p.S[2];
            RawImageLoader.ToneRgb(ref r0, ref g0, ref b0);
            if (Environment.GetEnvironmentVariable("PHOTOEDITOR_LOOK") is not null) RawLook.Apply(ref r0, ref g0, ref b0);
            var (l1, a1, b1) = HslMath.ToOklab(r0, g0, b0);
            var (l2, a2, b2) = HslMath.ToOklab(p.L[0], p.L[1], p.L[2]);
            return (H1: Hue(a1, b1), C1: MathF.Sqrt(a1 * a1 + b1 * b1), L1: l1, H2: Hue(a2, b2), C2: MathF.Sqrt(a2 * a2 + b2 * b2), L2: l2);
        }).Where(c => c.C1 > 0.04f && c.C2 > 0.02f).ToList();
        for (int hb = 0; hb < 24; hb++)
        {
            var bin = colour.Where(c => c.H1 >= hb * 15 && c.H1 < hb * 15 + 15).ToList();
            if (bin.Count < 200)
                continue;
            var dh = bin.Select(c => { float d = c.H2 - c.H1; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }).ToList();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {hb * 15,3}-{hb * 15 + 15,3}: dH {Median(dh),6:F1}  C {Median(bin.Select(c => c.C2 / c.C1).ToList()):F2}  L {Median(bin.Select(c => c.L1).ToList()):F2} → {Median(bin.Select(c => c.L2).ToList()):F2}  n={bin.Count}"));
        }

        // by chroma: does Lightroom stretch chroma (vivid colours more vivid, dull ones less) within the foliage hues?
        output.WriteLine("foliage hues 60-105 by OKLCh chroma of ours: C ratio LR / ours, dH, dL (n); and by lightness x chroma");
        var foliage = central.Select(p =>
        {
            float r0 = p.S[0], g0 = p.S[1], b0 = p.S[2];
            RawImageLoader.ToneRgb(ref r0, ref g0, ref b0);
            if (Environment.GetEnvironmentVariable("PHOTOEDITOR_LOOK") is not null) RawLook.Apply(ref r0, ref g0, ref b0);
            var (l1, a1, b1) = HslMath.ToOklab(r0, g0, b0);
            var (l2, a2, b2) = HslMath.ToOklab(p.L[0], p.L[1], p.L[2]);
            return (H1: Hue(a1, b1), C1: MathF.Sqrt(a1 * a1 + b1 * b1), L1: l1, H2: Hue(a2, b2), C2: MathF.Sqrt(a2 * a2 + b2 * b2), L2: l2);
        }).Where(c => c.H1 >= 60 && c.H1 < 105 && c.C1 > 0.01f).ToList();
        for (float c0 = 0.02f; c0 < 0.2f; c0 += 0.02f)
        {
            var bin = foliage.Where(c => c.C1 >= c0 && c.C1 < c0 + 0.02f).ToList();
            if (bin.Count < 200) continue;
            var dh = bin.Select(c => { float d = c.H2 - c.H1; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }).ToList();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  C {c0:F2}-{c0 + 0.02f:F2}: C ratio {Median(bin.Select(c => c.C2 / c.C1).ToList()):F3}  LR C {Median(bin.Select(c => c.C2).ToList()):F3}  dH {Median(dh),5:F1}  dL {Median(bin.Select(c => c.L2 - c.L1).ToList()):+0.000;-0.000}  n={bin.Count}"));
        }
        // spread: standard deviation of chroma and of hue in the foliage, ours vs Lightroom (same pixels)
        double Sd(IEnumerable<float> v) { var a = v.ToArray(); double m = a.Average(); return Math.Sqrt(a.Average(x => (x - m) * (x - m))); }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  foliage spread: chroma SD ours {Sd(foliage.Select(c => c.C1)):F4} LR {Sd(foliage.Select(c => c.C2)):F4}; hue SD ours {Sd(foliage.Select(c => c.H1)):F2} LR {Sd(foliage.Select(c => c.H2)):F2}; lightness SD ours {Sd(foliage.Select(c => c.L1)):F4} LR {Sd(foliage.Select(c => c.L2)):F4}"));
        foreach (var (lo, hi) in new[] { (0.01f, 0.04f), (0.04f, 0.08f), (0.08f, 0.3f) })
        {
            var part = foliage.Where(c => c.C1 >= lo && c.C1 < hi).ToList();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  chroma {lo:F2}-{hi:F2} (n={part.Count}): hue SD ours {Sd(part.Select(c => c.H1)):F2} LR {Sd(part.Select(c => c.H2)):F2}; chroma SD ours {Sd(part.Select(c => c.C1)):F4} LR {Sd(part.Select(c => c.C2)):F4}"));
        }

        // a 3 x 3 colour matrix (display-linear, after our rendering) fitted ours -> Lightroom by least squares, rows
        // normalised to keep white; then the foliage spread with it
        {
            var rendered3 = central.Select(p =>
            {
                float r0 = p.S[0], g0 = p.S[1], b0 = p.S[2];
                RawImageLoader.ToneRgb(ref r0, ref g0, ref b0);
                if (Environment.GetEnvironmentVariable("PHOTOEDITOR_LOOK") is not null) RawLook.Apply(ref r0, ref g0, ref b0);
                return (O: new[] { r0, g0, b0 }, L: p.L);
            }).Where(p => p.O.Max() < 0.95f && p.L.Max() < 0.95f && p.O.Max() > 0.005f).ToList();
            var ata = new double[3, 3];
            var atb = new double[3, 3];
            foreach (var (o, l) in rendered3)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                    {
                        ata[i, j] += o[i] * o[j];
                        atb[i, j] += o[i] * l[j];
                    }
            var inv = Invert3(ata);
            var m = new double[3, 3]; // l_j = sum_i m[i, j] o_i
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        m[i, j] += inv[i, k] * atb[k, j];
            output.WriteLine("fitted matrix (rows: output R, G, B; columns: input R, G, B; each row normalised to sum 1):");
            for (int j = 0; j < 3; j++)
            {
                double s = m[0, j] + m[1, j] + m[2, j];
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {m[0, j] / s:F4} {m[1, j] / s:F4} {m[2, j] / s:F4}   (row sum {s:F4})"));
            }
            var fol2 = rendered3.Select(p =>
            {
                var o = new float[3];
                for (int j = 0; j < 3; j++)
                {
                    double s = m[0, j] + m[1, j] + m[2, j];
                    o[j] = (float)((m[0, j] * p.O[0] + m[1, j] * p.O[1] + m[2, j] * p.O[2]) / s);
                }
                var (l1, a1, b1) = HslMath.ToOklab(Math.Max(o[0], 0), Math.Max(o[1], 0), Math.Max(o[2], 0));
                var (l2, a2, b2) = HslMath.ToOklab(p.L[0], p.L[1], p.L[2]);
                return (H1: Hue(a1, b1), C1: MathF.Sqrt(a1 * a1 + b1 * b1), H2: Hue(a2, b2), C2: MathF.Sqrt(a2 * a2 + b2 * b2));
            }).Where(c => c.H1 >= 60 && c.H1 < 105 && c.C1 > 0.01f).ToList();
            foreach (var (lo, hi) in new[] { (0.01f, 0.04f), (0.04f, 0.08f), (0.08f, 0.3f) })
            {
                var part = fol2.Where(c => c.C1 >= lo && c.C1 < hi).ToList();
                var dh = part.Select(c => { float d = c.H2 - c.H1; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }).ToList();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  with matrix, chroma {lo:F2}-{hi:F2} (n={part.Count}): hue SD ours {Sd(part.Select(c => c.H1)):F2} LR {Sd(part.Select(c => c.H2)):F2}; chroma SD ours {Sd(part.Select(c => c.C1)):F4} LR {Sd(part.Select(c => c.C2)):F4}; median dH {Median(dh):F1} C ratio {Median(part.Select(c => c.C2 / c.C1).ToList()):F2}"));
            }
        }

        // the same by lightness: does Lightroom's look depend on how bright a colour is (lit vs shaded leaves)?
        output.WriteLine("colour by OKLCh hue and OKLab lightness of ours: dH (deg) / C ratio LR / ours / dL (n)");
        for (int hb = 3; hb < 9; hb++)
        {
            var line = new System.Text.StringBuilder(string.Create(CultureInfo.InvariantCulture, $"  {hb * 15,3}-{hb * 15 + 15,3}:"));
            for (float l0 = 0.2f; l0 < 0.9f; l0 += 0.1f)
            {
                var bin = colour.Where(c => c.H1 >= hb * 15 && c.H1 < hb * 15 + 15 && c.L1 >= l0 && c.L1 < l0 + 0.1f).ToList();
                if (bin.Count < 100)
                {
                    line.Append(string.Create(CultureInfo.InvariantCulture, $"  L{l0:F1} -"));
                    continue;
                }
                var dh = bin.Select(c => { float d = c.H2 - c.H1; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }).ToList();
                line.Append(string.Create(CultureInfo.InvariantCulture,
                    $"  L{l0:F1} {Median(dh),5:F1}/{Median(bin.Select(c => c.C2 / c.C1).ToList()):F2}/{Median(bin.Select(c => c.L2 - c.L1).ToList()):+0.00;-0.00}({bin.Count})"));
            }
            output.WriteLine(line.ToString());
        }
    }

    /// <summary>z = (exposure·x)^power, extended Reinhard reaching 1 at the white scene value, then the toe.</summary>
    public static double CurveForm(double x, double[] q)
    {
        double z = Math.Pow(Math.Max(x, 0) * q[0], q[3]), wz = Math.Pow(q[0] * q[1], q[3]);
        double y = z * (1 + z / (wz * wz)) / (1 + z);
        return y * y * (1 + q[2]) / (y + q[2]);
    }

    public static double[] NelderMead(Func<double[], double> f, double[] start)
    {
        int n = start.Length;
        var simplex = new List<double[]> { start };
        for (int i = 0; i < n; i++)
        {
            var p = (double[])start.Clone();
            p[i] = p[i] == 0 ? 0.01 : p[i] * 1.2;
            simplex.Add(p);
        }
        for (int iter = 0; iter < 4000; iter++)
        {
            simplex.Sort((a, b) => f(a).CompareTo(f(b)));
            var centroid = new double[n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) centroid[j] += simplex[i][j] / n;
            double[] Along(double t) => centroid.Select((c, j) => c + t * (simplex[n][j] - c)).ToArray();
            var reflected = Along(-1);
            double fr = f(reflected);
            if (fr < f(simplex[0]))
            {
                var expanded = Along(-2);
                simplex[n] = f(expanded) < fr ? expanded : reflected;
            }
            else if (fr < f(simplex[n - 1]))
                simplex[n] = reflected;
            else
            {
                var contracted = Along(0.5);
                if (f(contracted) < f(simplex[n]))
                    simplex[n] = contracted;
                else
                    for (int i = 1; i <= n; i++)
                        simplex[i] = simplex[i].Select((v, j) => simplex[0][j] + 0.5 * (v - simplex[0][j])).ToArray();
            }
        }
        simplex.Sort((a, b) => f(a).CompareTo(f(b)));
        return simplex[0];
    }

    private static float Enc(float linear) => ColorMath.LinearToSrgbFast(Math.Clamp(linear, 0, 1)) * 255;

    private static double[,] Invert3(double[,] a)
    {
        double det = a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0])
            + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        var r = new double[3, 3];
        r[0, 0] = (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) / det;
        r[0, 1] = (a[0, 2] * a[2, 1] - a[0, 1] * a[2, 2]) / det;
        r[0, 2] = (a[0, 1] * a[1, 2] - a[0, 2] * a[1, 1]) / det;
        r[1, 0] = (a[1, 2] * a[2, 0] - a[1, 0] * a[2, 2]) / det;
        r[1, 1] = (a[0, 0] * a[2, 2] - a[0, 2] * a[2, 0]) / det;
        r[1, 2] = (a[0, 2] * a[1, 0] - a[0, 0] * a[1, 2]) / det;
        r[2, 0] = (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]) / det;
        r[2, 1] = (a[0, 1] * a[2, 0] - a[0, 0] * a[2, 1]) / det;
        r[2, 2] = (a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0]) / det;
        return r;
    }

    private static float Hue(float a, float b)
    {
        float hdeg = MathF.Atan2(b, a) * 180 / MathF.PI;
        return hdeg < 0 ? hdeg + 360 : hdeg;
    }

    private static float Median(List<float> v)
    {
        v.Sort();
        return v[v.Count / 2];
    }

    private static float Y(float[] c) => 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];

    private static readonly int Block = int.Parse(Environment.GetEnvironmentVariable("PHOTOEDITOR_BLOCK") ?? "2");

    private static float Avg(float[] img, int w, int x, int y, int c)
    {
        float s = 0;
        for (int j = 0; j < Block; j++)
            for (int i = 0; i < Block; i++)
                s += img[((y + j) * w + x + i) * 3 + c];
        return s / (Block * Block);
    }

    private static float Avg2(float[] img, int w, int x, int y, int c) =>
        (img[(y * w + x) * 3 + c] + img[(y * w + x + 1) * 3 + c] + img[((y + 1) * w + x) * 3 + c] + img[((y + 1) * w + x + 1) * 3 + c]) / 4;

    private static float[] Luminance(float[] rgb)
    {
        var y = new float[rgb.Length / 3];
        for (int i = 0; i < y.Length; i++)
            y[i] = 0.2126f * rgb[i * 3] + 0.7152f * rgb[i * 3 + 1] + 0.0722f * rgb[i * 3 + 2];
        return y;
    }

    /// <summary>Lightroom's Adobe RGB TIFF as linear sRGB floats (unclamped, so its wider gamut survives).</summary>
    private static (float[] Rgb, int W, int H) LoadAdobeRgb(string path)
    {
        using var image = new MagickImage(path);
        Assert.Contains("Adobe RGB", image.GetColorProfile()?.Description ?? "");
        var px = image.GetPixelsUnsafe().ToShortArray("RGB")!;
        int w = (int)image.Width, h = (int)image.Height;
        var result = new float[px.Length];
        var table = new float[65536];
        for (int i = 0; i < table.Length; i++) table[i] = MathF.Pow(i / 65535f, 563f / 256f);
        Parallel.For(0, w * h, i =>
        {
            float r = table[px[i * 3]], g = table[px[i * 3 + 1]], b = table[px[i * 3 + 2]];
            result[i * 3] = 1.39835574f * r - 0.39835574f * g;
            result[i * 3 + 1] = g;
            result[i * 3 + 2] = -0.04293930f * g + 1.04293930f * b;
        });
        return (result, w, h);
    }

    /// <summary>Shift of ours that best matches Lightroom's tile (normalised correlation of log luminance): coarse every 4 px, then ±4 px.</summary>
    private static (int, int, double) Align(float[] lr, float[] ours, int w, int tx, int ty)
    {
        double Score(int dx, int dy, int step)
        {
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
            int n = 0;
            for (int y = 0; y < Tile; y += step)
                for (int x = 0; x < Tile; x += step)
                {
                    double a = lr[(ty + y) * w + tx + x], b = ours[(ty + y + dy) * w + tx + x + dx];
                    sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
                    n++;
                }
            double cov = sab / n - sa / n * sb / n, va = saa / n - sa / n * sa / n, vb = sbb / n - sb / n * sb / n;
            return cov / Math.Sqrt(va * vb + 1e-12);
        }
        int range = Search / 4;
        var coarse = new double[(2 * range + 1) * (2 * range + 1)];
        Parallel.For(0, 2 * range + 1, j =>
        {
            for (int i = 0; i <= 2 * range; i++)
                coarse[j * (2 * range + 1) + i] = Score((i - range) * 4, (j - range) * 4, 4);
        });
        int best = Array.IndexOf(coarse, coarse.Max());
        int bx = (best % (2 * range + 1) - range) * 4, by = (best / (2 * range + 1) - range) * 4;
        double top = double.MinValue;
        (int, int) at = (bx, by);
        for (int dy = by - 4; dy <= by + 4; dy++)
            for (int dx = bx - 4; dx <= bx + 4; dx++)
            {
                if (Math.Abs(dx) > Search - 2 || Math.Abs(dy) > Search - 2)
                    continue;
                double s = Score(dx, dy, 1);
                if (s > top) { top = s; at = (dx, dy); }
            }
        return (at.Item1, at.Item2, top);
    }
}
