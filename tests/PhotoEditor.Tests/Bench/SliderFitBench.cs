using System.Globalization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// What one slider does, in Lightroom and in ours: two Lightroom TIFFs of the same RAW that differ by one slider are
/// perfectly aligned with each other, so the change per pixel is exact. For each pair: the tone transfer (luma before →
/// after), the chroma transfer (OKLCh chroma ratio by chroma and by lightness), the change of local detail per scale, and
/// how well the luminance change is predicted by the brightness of the pixel or of its surroundings at several blur
/// sizes (how local the slider is). Ours is measured the same way on two renders. Runs only when PHOTOEDITOR_BENCH and
/// PHOTOEDITOR_LR_DIR are set; PHOTOEDITOR_SLIDER picks one pair.
/// </summary>
public sealed class SliderFitBench(ITestOutputHelper output)
{
    private const string Sat = "IMG_5645 sharpening 40 saturation 65";
    private const string Vib = Sat + " vibrance 40";
    private const string Exp = Vib + " exposure 1";
    private const string Hl = Exp + " highlights -95";

    public static readonly (string Name, string Before, string After, AdjustmentSettings Ours, AdjustmentSettings OursAfter)[] Pairs =
    [
        ("Saturation 65", "IMG_5645 Sharpening default 40", Sat,
            AdjustmentSettings.RawDefault, AdjustmentSettings.RawDefault with { Saturation = 65 }),
        ("Vibrance 40", Sat, Vib,
            AdjustmentSettings.RawDefault with { Saturation = 65 }, AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40 }),
        ("Exposure +1", Vib, Exp,
            AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40 },
            AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40, Exposure = 1 }),
        ("Highlights -95", Exp, Hl,
            AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40, Exposure = 1 },
            AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40, Exposure = 1, Highlights = -95 }),
    ];

    [Fact]
    public void Measure()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        if (raw is null || dir is null)
            return;
        var only = Environment.GetEnvironmentVariable("PHOTOEDITOR_SLIDER");
        var oursOverride = Environment.GetEnvironmentVariable("PHOTOEDITOR_OURS_VALUE");
        using var photo = RawImageLoader.Load(raw);
        var lens = Core.Lens.PhotoLens.Of(raw);
        foreach (var (name, before, after, ours, oursAfter0) in Pairs)
        {
            if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase))
                continue;
            var oursAfter = oursAfter0;
            if (oursOverride is not null)
            {
                double v = double.Parse(oursOverride, CultureInfo.InvariantCulture);
                oursAfter = name.Split(' ')[0] switch
                {
                    "Saturation" => oursAfter with { Saturation = v },
                    "Vibrance" => oursAfter with { Vibrance = v },
                    "Exposure" => oursAfter with { Exposure = v },
                    "Highlights" => oursAfter with { Highlights = v },
                    _ => oursAfter,
                };
            }
            output.WriteLine($"==== {name}" + (oursOverride is null ? "" : $" (ours at {oursOverride})"));
            using var lrA = TiffImageLoader.Load(Path.Combine(dir, before + ".tif"));
            using var lrB = TiffImageLoader.Load(Path.Combine(dir, after + ".tif"));
            using var ourA = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = ours }, lens);
            using var ourB = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = oursAfter }, lens);
            Report("LR  ", Planes.Of(lrA), Planes.Of(lrB));
            Report("ours", Planes.Of(ourA), Planes.Of(ourB));
        }
    }

    /// <summary>
    /// Highlights check (the constants were fitted by a grid over these three values): PHOTOEDITOR_GRID set to anything
    /// runs it; prints the tone RMS against Lightroom's −95 and the detail per scale.
    /// </summary>
    [Fact]
    public void HighlightsGrid()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        var grid = Environment.GetEnvironmentVariable("PHOTOEDITOR_GRID");
        if (raw is null || dir is null || grid is null)
            return;
        var (_, before, after, ours, oursAfter) = Pairs.Single(p => p.Name.StartsWith("Highlights"));
        using var photo = RawImageLoader.Load(raw);
        var lens = Core.Lens.PhotoLens.Of(raw);
        Planes lrA, lrB, ourA;
        using (var a = TiffImageLoader.Load(Path.Combine(dir, before + ".tif"))) lrA = Planes.Of(a);
        using (var b = TiffImageLoader.Load(Path.Combine(dir, after + ".tif"))) lrB = Planes.Of(b);
        using (var a = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = ours }, lens)) ourA = Planes.Of(a);
        var lrTone = ToneBins(lrA, lrB);
        var lrDetail = Detail(lrA, lrB);
        output.WriteLine($"LR detail {string.Join(" ", lrDetail.Select(F2))}");
        Planes ourB;
        using (var b = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = oursAfter }, lens)) ourB = Planes.Of(b);
        var tone = ToneBins(ourA, ourB);
        double rms = Math.Sqrt(Enumerable.Range(0, 16).Where(i => !float.IsNaN(lrTone[i]) && !float.IsNaN(tone[i]))
            .Average(i => (tone[i] - lrTone[i]) * (tone[i] - lrTone[i])));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"top {ToneCurve.HighlightsTopStops} keep {ToneCurve.HighlightsDetailKeep} max {ToneBaseMap.MaxDetailStops}: tone rms {rms:F1} (top bins {tone[12]:F0}/{tone[15]:F0} vs {lrTone[12]:F0}/{lrTone[15]:F0}) detail {string.Join(" ", Detail(ourA, ourB).Select(F2))}"));
    }

    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static float[] ToneBins(Planes a, Planes b)
    {
        var tone = new List<float>[16];
        for (int i = 0; i < 16; i++) tone[i] = [];
        for (int i = 0; i < a.SmallLuma.Length; i++)
            tone[Math.Min(15, (int)(a.SmallLuma[i] * 16))].Add(b.SmallLuma[i]);
        return tone.Select(l => l.Count < 50 ? float.NaN : Median(l) * 255).ToArray();
    }

    private static double[] Detail(Planes a, Planes b)
    {
        var bandsA = new double[4];
        var bandsB = new double[4];
        foreach (var (cx, cy) in Crops)
        {
            var ea = Bands(a.Luma, a.Width, cx, cy);
            var eb = Bands(b.Luma, b.Width, cx, cy);
            for (int k = 0; k < 4; k++) { bandsA[k] += ea[k]; bandsB[k] += eb[k]; }
        }
        return bandsA.Select((v, k) => bandsB[k] / v).ToArray();
    }

    private void Report(string who, Planes a, Planes b)
    {
        string F(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
        string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        // tone transfer on 1/4 size
        var tone = new List<float>[16];
        for (int i = 0; i < 16; i++) tone[i] = [];
        for (int i = 0; i < a.SmallLuma.Length; i++)
            tone[Math.Min(15, (int)(a.SmallLuma[i] * 16))].Add(b.SmallLuma[i]);
        output.WriteLine($"  {who} tone (before 0..255 in 16 bins → median after): " +
            string.Join(" ", tone.Select((l, i) => l.Count < 50 ? "-" : $"{F(i * 16 + 8)}→{F(Median(l) * 255)}")));
        // chroma ratio by before chroma and by lightness
        var byChroma = new List<float>[6];
        var byLight = new List<float>[5];
        for (int i = 0; i < 6; i++) byChroma[i] = [];
        for (int i = 0; i < 5; i++) byLight[i] = [];
        for (int i = 0; i < a.SmallChroma.Length; i++)
        {
            if (a.SmallChroma[i] < 0.01f) continue;
            float ratio = b.SmallChroma[i] / a.SmallChroma[i];
            byChroma[Math.Min(5, (int)(a.SmallChroma[i] / 0.03f))].Add(ratio);
            if (a.SmallChroma[i] > 0.03f) byLight[Math.Min(4, (int)(a.SmallLightness[i] * 5))].Add(ratio);
        }
        output.WriteLine($"  {who} chroma ratio by before chroma (0.03 steps, 0.01-0.03, …, >0.15): " +
            string.Join(" ", byChroma.Select(l => l.Count < 50 ? "-" : F2(Median(l)))));
        output.WriteLine($"  {who} chroma ratio by OKLab lightness (0.2 steps; chroma > 0.03): " +
            string.Join(" ", byLight.Select(l => l.Count < 50 ? "-" : F2(Median(l)))));
        // detail per scale, on full-resolution crops
        var bandsA = new double[4];
        var bandsB = new double[4];
        foreach (var (cx, cy) in Crops)
        {
            var ea = Bands(a.Luma, a.Width, cx, cy);
            var eb = Bands(b.Luma, b.Width, cx, cy);
            for (int k = 0; k < 4; k++) { bandsA[k] += ea[k]; bandsB[k] += eb[k]; }
        }
        output.WriteLine($"  {who} detail after / before, fine to coarse (σ 0.7-1.4-2.8-5.6-11): " +
            string.Join(" ", bandsA.Select((v, k) => F2(bandsB[k] / v))));
        // how local: correlation of the log luminance change with the log brightness around, per blur size
        var logGain = new float[a.SmallLuma.Length];
        var linA = new float[a.SmallLuma.Length];
        for (int i = 0; i < logGain.Length; i++)
        {
            float ya = ColorMath.SrgbToLinear(a.SmallLuma[i]), yb = ColorMath.SrgbToLinear(b.SmallLuma[i]);
            linA[i] = MathF.Log2(ya + 1e-3f);
            logGain[i] = MathF.Log2(yb + 1e-3f) - linA[i];
        }
        var corr = new List<string>();
        foreach (var sigma in new[] { 0.0, 2, 8, 32 })
        {
            var around = sigma == 0 ? linA : Blur(linA, a.SmallWidth, a.SmallLuma.Length / a.SmallWidth, sigma);
            corr.Add($"{sigma * 4}px r={F2(Correlation(around, logGain))}");
        }
        output.WriteLine($"  {who} log luminance change vs log brightness of the area (blur σ at full size): {string.Join("  ", corr)}");
        output.WriteLine($"  {who} mean luminance change: {F2(logGain.Average())} stops; p10 {F2(Percentile(logGain, 0.1))} p50 {F2(Percentile(logGain, 0.5))} p90 {F2(Percentile(logGain, 0.9))}");
    }

    private static readonly (int, int)[] Crops = [(2000, 3000), (2600, 2300), (1200, 4500), (3300, 2000), (900, 1500), (3200, 1500)];

    private sealed class Planes
    {
        public required float[] Luma { get; init; }
        public required int Width { get; init; }
        public required float[] SmallLuma { get; init; }
        public required float[] SmallChroma { get; init; }
        public required float[] SmallLightness { get; init; }
        public required int SmallWidth { get; init; }

        /// <summary>Full-size sRGB luma (0..1) and 1/4-size (4 × 4 linear average) luma, OKLCh chroma and OKLab lightness.</summary>
        public static Planes Of(SKBitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height, sw = w / 4, sh = h / 4;
            var pixels = bitmap.Pixels;
            var luma = new float[w * h];
            for (int i = 0; i < luma.Length; i++)
                luma[i] = (0.2126f * pixels[i].Red + 0.7152f * pixels[i].Green + 0.0722f * pixels[i].Blue) / 255f;
            var sl = new float[sw * sh];
            var sc = new float[sw * sh];
            var sL = new float[sw * sh];
            Parallel.For(0, sh, y =>
            {
                for (int x = 0; x < sw; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int j = 0; j < 4; j++)
                        for (int i = 0; i < 4; i++)
                        {
                            var c = pixels[(y * 4 + j) * w + x * 4 + i];
                            r += ColorMath.SrgbByteToLinear(c.Red);
                            g += ColorMath.SrgbByteToLinear(c.Green);
                            b += ColorMath.SrgbByteToLinear(c.Blue);
                        }
                    r /= 16; g /= 16; b /= 16;
                    int k = y * sw + x;
                    sl[k] = ColorMath.LinearToSrgb(0.2126f * r + 0.7152f * g + 0.0722f * b);
                    var (l, ca, cb) = HslMath.ToOklab(r, g, b);
                    sc[k] = MathF.Sqrt(ca * ca + cb * cb);
                    sL[k] = l;
                }
            });
            return new Planes { Luma = luma, Width = w, SmallLuma = sl, SmallChroma = sc, SmallLightness = sL, SmallWidth = sw };
        }
    }

    private static float Median(List<float> v)
    {
        v.Sort();
        return v[v.Count / 2];
    }

    private static float Percentile(float[] v, double p)
    {
        var s = (float[])v.Clone();
        Array.Sort(s);
        return s[(int)(p * (s.Length - 1))];
    }

    private static double Correlation(float[] x, float[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxy / Math.Sqrt(sxx * syy + 1e-12);
    }

    private static double[] Bands(float[] luma, int width, int cx, int cy)
    {
        const int size = 320;
        var crop = new float[size * size];
        for (int y = 0; y < size; y++)
            Array.Copy(luma, (cy - size / 2 + y) * width + cx - size / 2, crop, y * size, size);
        var sigmas = new[] { 0.7, 1.4, 2.8, 5.6, 11.2 };
        var levels = sigmas.Select(s => Blur(crop, size, size, s)).ToList();
        var result = new double[4];
        const int m = 40;
        for (int k = 0; k < 4; k++)
        {
            double e = 0;
            for (int y = m; y < size - m; y++)
                for (int x = m; x < size - m; x++)
                {
                    double d = levels[k][y * size + x] - levels[k + 1][y * size + x];
                    e += d * d;
                }
            result[k] = Math.Sqrt(e);
        }
        return result;
    }

    private static float[] Blur(float[] img, int w, int h, double sigma)
    {
        int r = (int)Math.Ceiling(sigma * 3);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(i => (float)Math.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        float ks = k.Sum();
        for (int i = 0; i < k.Length; i++) k[i] /= ks;
        var tmp = new float[img.Length];
        var res = new float[img.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * img[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = s;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                res[y * w + x] = s;
            }
        });
        return res;
    }
}
