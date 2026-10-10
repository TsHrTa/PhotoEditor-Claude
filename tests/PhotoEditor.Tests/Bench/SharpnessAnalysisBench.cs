using System.Globalization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// Why is our render softer than Lightroom's? Aligns crops of ours to the Lightroom TIFF (translation search), maps our
/// luma to Lightroom's through a fitted curve (removes the tone difference), and compares the detail per frequency band
/// (differences of Gaussians). A ratio that grows towards the finest band means resolution / sharpening, a flat one
/// means contrast. Runs only when PHOTOEDITOR_BENCH and PHOTOEDITOR_LR_DIR are set.
/// </summary>
public sealed class SharpnessAnalysisBench(ITestOutputHelper output)
{
    [Fact]
    public void Analyse()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        if (raw is null || dir is null)
            return;
        var lrFile = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_FILE") ?? "IMG_5645 sharpening 0";
        using var lr = TiffImageLoader.Load(Path.Combine(dir, lrFile + ".tif"));
        var lrLuma = Luma(lr);
        var lens = Core.Lens.PhotoLens.Of(raw);
        output.WriteLine($"lens: {lens}");
        bool lensProfile = Environment.GetEnvironmentVariable("PHOTOEDITOR_NO_LENS") is null;
        var variants = new List<(string Name, RawDemosaic Demosaic, AdjustmentSettings Settings)>();
        foreach (var demosaic in (Environment.GetEnvironmentVariable("PHOTOEDITOR_DEMOSAICS") ?? "Dcb").Split(','))
            variants.Add(($"{demosaic} sharpen 0", Enum.Parse<RawDemosaic>(demosaic), AdjustmentSettings.RawDefault with { SharpenAmount = 0 }));
        if (Environment.GetEnvironmentVariable("PHOTOEDITOR_EXTRA") is { } extra)
            foreach (var part in extra.Split(';'))
            {
                var v = part.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                variants.Add(($"Dcb sharpen {v[0]} radius {v[1]} detail {v[2]}", RawDemosaic.Dcb,
                    AdjustmentSettings.RawDefault with { SharpenAmount = v[0], SharpenRadius = v[1], SharpenDetail = v[2] }));
            }
        var centres = new[] { (2000, 3000), (2600, 2300), (1200, 4500), (3300, 2000), (900, 1500) };
        var photos = new Dictionary<RawDemosaic, SKBitmap>();
        foreach (var (name, demosaic, settings) in variants)
        {
            if (!photos.TryGetValue(demosaic, out var photo))
                photos[demosaic] = photo = RawImageLoader.Load(raw, demosaic);
            using var ours = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = settings with { LensProfile = lensProfile } }, lens);
            var ourLuma = Luma(ours);
            output.WriteLine($"== {name}");
            var sum = new double[5];
            foreach (var (cx, cy) in centres)
            {
                const int size = 320;
                var (dx, dy) = Align(lrLuma, ourLuma, lr.Width, cx, cy, size);
                var a = Crop(lrLuma, lr.Width, cx - size / 2, cy - size / 2, size);
                var b = Crop(ourLuma, ours.Width, cx - size / 2 + dx, cy - size / 2 + dy, size);
                var mapped = MatchTone(b, a);
                var ea = Bands(a, size);
                var em = Bands(mapped, size);
                var ratios = ea.Zip(em, (x, y) => x / y).ToArray();
                for (int i = 0; i < ratios.Length; i++) sum[i] += Math.Log(ratios[i]);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  ({cx},{cy}) shift ({dx},{dy}) LR/ours band energy fine to coarse: {Join(ratios)}"));
            }
            output.WriteLine("  geometric mean: " + Join(sum.Select(s => Math.Exp(s / centres.Length))));
        }
        foreach (var photo in photos.Values) photo.Dispose();
    }

    private static string Join(IEnumerable<double> values) =>
        string.Join(" ", values.Select(r => r.ToString("F2", CultureInfo.InvariantCulture)));

    private static float[] Luma(SKBitmap bitmap)
    {
        var result = new float[bitmap.Width * bitmap.Height];
        var pixels = bitmap.Pixels;
        for (int i = 0; i < result.Length; i++)
            result[i] = (0.2126f * pixels[i].Red + 0.7152f * pixels[i].Green + 0.0722f * pixels[i].Blue) / 255f;
        return result;
    }

    private static float[] Crop(float[] luma, int width, int x0, int y0, int size)
    {
        var result = new float[size * size];
        for (int y = 0; y < size; y++)
            Array.Copy(luma, (y0 + y) * width + x0, result, y * size, size);
        return result;
    }

    /// <summary>Offset of ours that best matches Lightroom's crop (normalised correlation of the luma, ±40 px).</summary>
    private static (int, int) Align(float[] lr, float[] ours, int width, int cx, int cy, int size)
    {
        var a = Normalise(Crop(lr, width, cx - size / 2, cy - size / 2, size));
        var scores = new double[81 * 81];
        Parallel.For(0, 81, j =>
        {
            int dy = j - 40;
            for (int dx = -40; dx <= 40; dx++)
            {
                var b = Normalise(Crop(ours, width, cx - size / 2 + dx, cy - size / 2 + dy, size));
                double c = 0;
                for (int i = 0; i < a.Length; i += 3) c += a[i] * b[i];
                scores[j * 81 + dx + 40] = c;
            }
        });
        int best = Array.IndexOf(scores, scores.Max());
        return (best % 81 - 40, best / 81 - 40);
    }

    private static float[] Normalise(float[] v)
    {
        float mean = v.Average(), sd = MathF.Sqrt(v.Select(x => (x - mean) * (x - mean)).Average()) + 1e-6f;
        return v.Select(x => (x - mean) / sd).ToArray();
    }

    /// <summary>
    /// Our luma mapped so its histogram equals Lightroom's (quantile matching: rank preserved). Unlike a fitted
    /// per-pixel curve it is not flattened by alignment scatter.
    /// </summary>
    private static float[] MatchTone(float[] ours, float[] lr)
    {
        if (Environment.GetEnvironmentVariable("PHOTOEDITOR_FIT_CURVE") is not null)
            return FitCurve(ours, lr);
        var order = Enumerable.Range(0, ours.Length).OrderBy(i => ours[i]).ToArray();
        var sorted = lr.OrderBy(v => v).ToArray();
        var result = new float[ours.Length];
        for (int r = 0; r < order.Length; r++) result[order[r]] = sorted[r];
        return result;
    }

    private static float[] FitCurve(float[] ours, float[] lr)
    {
        const int bins = 32;
        var sum = new double[bins];
        var count = new int[bins];
        for (int i = 0; i < ours.Length; i++)
        {
            int k = Math.Min(bins - 1, (int)(ours[i] * bins));
            sum[k] += lr[i];
            count[k]++;
        }
        var curve = new double[bins];
        for (int k = 0; k < bins; k++) curve[k] = count[k] > 20 ? sum[k] / count[k] : double.NaN;
        for (int k = 0; k < bins; k++)
            if (double.IsNaN(curve[k]))
            {
                int l = k - 1, r = k + 1;
                while (l >= 0 && double.IsNaN(curve[l])) l--;
                while (r < bins && double.IsNaN(curve[r])) r++;
                curve[k] = l >= 0 && r < bins ? (curve[l] + curve[r]) / 2 : l >= 0 ? curve[l] : r < bins ? curve[r] : 0;
            }
        return ours.Select(v =>
        {
            double p = v * bins - 0.5;
            int k = Math.Clamp((int)Math.Floor(p), 0, bins - 2);
            double t = Math.Clamp(p - k, 0, 1);
            return (float)(curve[k] + (curve[k + 1] - curve[k]) * t);
        }).ToArray();
    }

    /// <summary>RMS of the bands I − G(0.7), G(0.7) − G(1.4), G(1.4) − G(2.8), G(2.8) − G(5.6), G(5.6) − G(11.2), inner part only.</summary>
    private static double[] Bands(float[] img, int size)
    {
        var sigmas = new[] { 0.7, 1.4, 2.8, 5.6, 11.2 };
        var levels = new List<float[]> { img };
        levels.AddRange(sigmas.Select(s => Blur(img, size, s)));
        var result = new double[sigmas.Length];
        const int m = 40;
        for (int b = 0; b < sigmas.Length; b++)
        {
            double e = 0;
            int n = 0;
            for (int y = m; y < size - m; y++)
                for (int x = m; x < size - m; x++)
                {
                    double d = levels[b][y * size + x] - levels[b + 1][y * size + x];
                    e += d * d;
                    n++;
                }
            result[b] = Math.Sqrt(e / n);
        }
        return result;
    }

    private static float[] Blur(float[] img, int size, double sigma)
    {
        int r = (int)Math.Ceiling(sigma * 3);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(i => Math.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        double ks = k.Sum();
        var tmp = new float[img.Length];
        var res = new float[img.Length];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * img[y * size + Math.Clamp(x + i, 0, size - 1)];
                tmp[y * size + x] = (float)(s / ks);
            }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * tmp[Math.Clamp(y + i, 0, size - 1) * size + x];
                res[y * size + x] = (float)(s / ks);
            }
        return res;
    }
}
