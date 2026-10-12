using System.Globalization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// Our render of a RAW against Lightroom TIFF exports of the same file with known settings: overviews, side-by-side
/// 100 % crops and numbers per region. Runs only when PHOTOEDITOR_BENCH (the RAW), PHOTOEDITOR_LR_DIR (the TIFFs)
/// and PHOTOEDITOR_BENCH_OUT are set. PHOTOEDITOR_LR_REGIONS: "x,y,size;x,y,size;…" (full-resolution pixels).
/// </summary>
public sealed class LightroomCompareBench(ITestOutputHelper output)
{
    /// <summary>Lightroom file name (without extension) → our settings for the same edit.</summary>
    public static readonly (string File, AdjustmentSettings Ours)[] Cases =
    [
        ("IMG_5645 sharpening 0", AdjustmentSettings.RawDefault with { SharpenAmount = 0 }),
        ("IMG_5645 sharpening 1", AdjustmentSettings.RawDefault with { SharpenAmount = 1 }),
        ("IMG_5645 sharpening 25", AdjustmentSettings.RawDefault with { SharpenAmount = 25 }),
        ("IMG_5645 Sharpening default 40", AdjustmentSettings.RawDefault),
        ("IMG_5645 sharpening 40 saturation 65", AdjustmentSettings.RawDefault with { Saturation = 65 }),
        ("IMG_5645 sharpening 40 saturation 65 vibrance 40", AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40 }),
        ("IMG_5645 sharpening 40 saturation 65 vibrance 40 exposure 1",
            AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40, Exposure = 1 }),
        ("IMG_5645 sharpening 40 saturation 65 vibrance 40 exposure 1 highlights -95",
            AdjustmentSettings.RawDefault with { Saturation = 65, Vibrance = 40, Exposure = 1, Highlights = -95 }),
    ];

    [Fact]
    public void Compare()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        var outDir = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH_OUT");
        if (raw is null || dir is null || outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        var only = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_ONLY");
        var regions = ParseRegions(Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_REGIONS"));
        using var photo = RawImageLoader.Load(raw);
        foreach (var (file, settings) in Cases)
        {
            if (only is not null && !file.Contains(only, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(dir, file + ".tif");
            if (!File.Exists(path))
                continue;
            using var lr = TiffImageLoader.Load(path);
            var ours = settings;
            if (Environment.GetEnvironmentVariable("PHOTOEDITOR_OURS_SHARPEN") is { } sharpen)
                ours = ours with { SharpenAmount = double.Parse(sharpen, CultureInfo.InvariantCulture) };
            if (Environment.GetEnvironmentVariable("PHOTOEDITOR_OURS_CA") is { } ca) // 0 or 1; on by default for RAWs
                ours = ours with { RemoveChromaticAberration = ca == "1" };
            using var mine = CpuAdjustmentRenderer.Render(photo, new Core.Editing.EditState { Adjustments = ours }, Core.Lens.PhotoLens.Of(raw));
            output.WriteLine($"== {file} (LR {lr.Width} x {lr.Height}, ours {mine.Width} x {mine.Height})");
            WriteOverview(lr, mine, Path.Combine(outDir, Slug(file) + "_overview.png"));
            output.WriteLine($"  luma percentiles 1/5/25/50/75/95/99/99.9: LR {Percentiles(lr)}");
            output.WriteLine($"  {"",-38}ours {Percentiles(mine)}");
            output.WriteLine($"  median luma by radius (0.1 steps): LR {RadialMedians(lr)}");
            output.WriteLine($"  {"",-29}ours {RadialMedians(mine)}");
            output.WriteLine($"  mean chroma by luma band (0-32-64-…-256): LR {ChromaByLuma(lr)}");
            output.WriteLine($"  {"",-40}ours {ChromaByLuma(mine)}");
            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                var lrStats = Measure(lr, r);
                var ourStats = Measure(mine, r);
                output.WriteLine($"  region {i} ({r.Left},{r.Top},{r.Width}): LR {lrStats}");
                output.WriteLine($"  {"",-24}ours {ourStats}");
                WriteSideBySide(lr, mine, r, Path.Combine(outDir, $"{Slug(file)}_r{i}.png"));
            }
        }
    }

    public readonly record struct Stats(double Luma, double Gradient, double Chroma, double R, double G, double B, double OkChroma = 0, double OkHue = 0)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"luma {Luma:F1} grad {Gradient:F2} chroma {Chroma:F1} rgb ({R:F0},{G:F0},{B:F0}) OKLCh C {OkChroma:F3} h {OkHue:F0}");
    }

    /// <summary>Mean luma (0..255), mean gradient magnitude of the luma, mean chroma (max − min channel), mean RGB.</summary>
    public static Stats Measure(SKBitmap bitmap, SKRectI r)
    {
        double luma = 0, grad = 0, chroma = 0, sr = 0, sg = 0, sb = 0, oa = 0, ob = 0;
        int n = 0, ng = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                var c = bitmap.GetPixel(x, y);
                double l = Luma(c);
                luma += l;
                chroma += Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                sr += c.Red; sg += c.Green; sb += c.Blue;
                var (_, la, lb) = HslMath.ToOklab(ColorMath.SrgbByteToLinear(c.Red), ColorMath.SrgbByteToLinear(c.Green), ColorMath.SrgbByteToLinear(c.Blue));
                oa += la; ob += lb;
                n++;
                if (x + 1 < r.Right && y + 1 < r.Bottom)
                {
                    double dx = Luma(bitmap.GetPixel(x + 1, y)) - l, dy = Luma(bitmap.GetPixel(x, y + 1)) - l;
                    grad += Math.Sqrt(dx * dx + dy * dy);
                    ng++;
                }
            }
        return new(luma / n, grad / ng, chroma / n, sr / n, sg / n, sb / n, Math.Sqrt(oa * oa + ob * ob) / n,
            (Math.Atan2(ob, oa) * 180 / Math.PI + 360) % 360);
    }

    private static string Percentiles(SKBitmap bitmap)
    {
        var hist = new long[256];
        long n = 0;
        for (int y = 0; y < bitmap.Height; y += 4)
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                hist[(int)Math.Round(Luma(bitmap.GetPixel(x, y)))]++;
                n++;
            }
        var result = new List<string>();
        foreach (var p in new[] { 0.01, 0.05, 0.25, 0.5, 0.75, 0.95, 0.99, 0.999 })
        {
            long target = (long)(p * n), acc = 0;
            int v = 0;
            while (v < 255 && (acc += hist[v]) < target) v++;
            result.Add(v.ToString(CultureInfo.InvariantCulture));
        }
        return string.Join(" ", result);
    }

    private static string RadialMedians(SKBitmap bitmap)
    {
        var bands = Enumerable.Range(0, 10).Select(_ => new List<double>()).ToArray();
        float cx = bitmap.Width / 2f, cy = bitmap.Height / 2f, half = MathF.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < bitmap.Height; y += 4)
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                float r = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / half;
                bands[Math.Min(9, (int)(r * 10))].Add(Luma(bitmap.GetPixel(x, y)));
            }
        return string.Join(" ", bands.Select(b => b.Count == 0 ? "-" : b.Order().ElementAt(b.Count / 2).ToString("F0", CultureInfo.InvariantCulture)));
    }

    private static string ChromaByLuma(SKBitmap bitmap)
    {
        var sum = new double[8];
        var count = new long[8];
        for (int y = 0; y < bitmap.Height; y += 4)
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                var c = bitmap.GetPixel(x, y);
                int band = Math.Min(7, (int)(Luma(c) / 32));
                sum[band] += Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                count[band]++;
            }
        return string.Join(" ", sum.Select((s, i) => count[i] == 0 ? "-" : (s / count[i]).ToString("F0", CultureInfo.InvariantCulture)));
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    private static List<SKRectI> ParseRegions(string? text)
    {
        var list = new List<SKRectI>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = part.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            list.Add(new SKRectI(v[0], v[1], v[0] + v[2], v[1] + v[2]));
        }
        return list;
    }

    private static string Slug(string file) => file.Replace("IMG_5645 ", "").Replace(' ', '_');

    private static void WriteOverview(SKBitmap lr, SKBitmap mine, string path)
    {
        const int w = 900;
        int h = lr.Height * w / lr.Width;
        using var surface = SKSurface.Create(new SKImageInfo(w * 2 + 10, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using (var a = SKImage.FromBitmap(lr)) canvas.DrawImage(a, new SKRect(0, 0, w, h), sampling);
        using (var b = SKImage.FromBitmap(mine)) canvas.DrawImage(b, new SKRect(w + 10, 0, w * 2 + 10, h), sampling);
        Save(surface, path);
    }

    private static void WriteSideBySide(SKBitmap lr, SKBitmap mine, SKRectI r, string path)
    {
        // PHOTOEDITOR_LR_ZOOM: pixel-sized enlargement of small crops (nearest neighbour)
        int z = int.TryParse(Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_ZOOM"), out var zoom) && zoom > 1 ? zoom : 1;
        int w = r.Width * z, h = r.Height * z;
        using var surface = SKSurface.Create(new SKImageInfo(w * 2 + 6, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest);
        using (var a = SKImage.FromBitmap(lr)) canvas.DrawImage(a, r, new SKRect(0, 0, w, h), nearest);
        using (var b = SKImage.FromBitmap(mine)) canvas.DrawImage(b, r, new SKRect(w + 6, 0, w * 2 + 6, h), nearest);
        Save(surface, path);
    }

    private static void Save(SKSurface surface, string path)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
