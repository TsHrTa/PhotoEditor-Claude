using System.Diagnostics;
using System.Globalization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// LibRaw's demosaicing (DCB, DHT, Linear) against our C# ports (bilinear, RCD, AMaZE) on the same RAW, rendered with the
/// RAW defaults: decode time, fine detail and false colour on leaf crops, and side-by-side 2 × crops per region (the
/// Lightroom TIFF aligned as a reference column when PHOTOEDITOR_LR_DIR is set). Runs only when PHOTOEDITOR_BENCH and
/// PHOTOEDITOR_BENCH_OUT are set.
/// </summary>
public sealed class DemosaicCompareBench(ITestOutputHelper output)
{
    private static readonly RawDemosaic[] Methods =
        [RawDemosaic.Dcb, RawDemosaic.Dht, RawDemosaic.Linear, RawDemosaic.BilinearCs, RawDemosaic.Rcd, RawDemosaic.Amaze];

    /// <summary>Crop centres (full resolution, upright 4000 × 6000): leaves against the sky, dense foliage, branches, ground.</summary>
    private static readonly (int X, int Y, string Name)[] Regions =
        [(3250, 1900, "leaves-sky"), (2700, 2300, "foliage"), (1450, 900, "branches-sky"), (2000, 4700, "ground")];

    private const int Crop = 220, Zoom = 2;

    [Fact]
    public void Compare()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var outDir = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH_OUT");
        if (raw is null || outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        var settings = Environment.GetEnvironmentVariable("PHOTOEDITOR_NO_SHARPEN") is null
            ? AdjustmentSettings.RawDefault
            : AdjustmentSettings.RawDefault with { SharpenAmount = 0 };
        var lens = Core.Lens.PhotoLens.Of(raw);
        var renders = new List<(string Name, SKBitmap Image)>();
        foreach (var method in Methods)
        {
            var w = Stopwatch.StartNew();
            using var photo = RawImageLoader.Load(raw, method);
            long decode = w.ElapsedMilliseconds;
            renders.Add((method.ToString(), CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = settings }, lens)));
            output.WriteLine($"{method}: decode {decode} ms");
        }
        var lrDir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        var lrFile = lrDir is null ? null : Path.Combine(lrDir, "IMG_5645 Sharpening default 40.tif");
        SKBitmap? lr = lrFile is not null && File.Exists(lrFile) ? TiffImageLoader.Load(lrFile) : null;

        // the hand-over check: our bilinear on LibRaw's mosaic against LibRaw's own Linear
        var linear = renders.Single(r => r.Name == nameof(RawDemosaic.Linear)).Image;
        var bilinear = renders.Single(r => r.Name == nameof(RawDemosaic.BilinearCs)).Image;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"hand-over check, BilinearCs vs LibRaw Linear: mean |difference| {MeanAbsDifference(linear, bilinear):F2} levels"));

        output.WriteLine("per region: fine detail (luma DoG energy, sigma 0.7-1.4 / 1.4-2.8) and false colour (fine OKLab a, b energy x 1000)");
        foreach (var (x, y, name) in Regions)
        {
            var rect = new SKRectI(x - Crop / 2, y - Crop / 2, x + Crop / 2, y + Crop / 2);
            var columns = renders.Select(r => (r.Name, Bitmap: r.Image, Rect: rect)).ToList();
            foreach (var (method, image, _) in columns)
            {
                var (fine, medium, chroma) = Measure(image, rect);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {name,-13} {method,-10} detail {fine:F2} / {medium:F2}   false colour {chroma:F2}"));
            }
            if (lr is not null)
            {
                var (dx, dy) = Align(lr, linear, rect);
                var lrRect = new SKRectI(rect.Left - dx, rect.Top - dy, rect.Right - dx, rect.Bottom - dy);
                var (fine, medium, chroma) = Measure(lr, lrRect);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {name,-13} {"Lightroom",-10} detail {fine:F2} / {medium:F2}   false colour {chroma:F2}   (shift {dx},{dy})"));
                columns.Add(("Lightroom", lr, lrRect));
            }
            WriteGrid(columns, Path.Combine(outDir, $"demosaic_{name}.png"));
        }
        foreach (var r in renders) r.Image.Dispose();
        lr?.Dispose();
    }

    private static double MeanAbsDifference(SKBitmap a, SKBitmap b)
    {
        double sum = 0;
        long n = 0;
        for (int y = 100; y < a.Height - 100; y += 3)
            for (int x = 100; x < a.Width - 100; x += 3)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
                n += 3;
            }
        return sum / n;
    }

    private static (double Fine, double Medium, double Chroma) Measure(SKBitmap bitmap, SKRectI r)
    {
        int w = r.Width, h = r.Height;
        var luma = new float[w * h];
        var ca = new float[w * h];
        var cb = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bitmap.GetPixel(r.Left + x, r.Top + y);
                float lr = ColorMath.SrgbToLinear(c.Red / 255f), lg = ColorMath.SrgbToLinear(c.Green / 255f), lb = ColorMath.SrgbToLinear(c.Blue / 255f);
                var (l, a, b) = HslMath.ToOklab(lr, lg, lb);
                luma[y * w + x] = l;
                ca[y * w + x] = a;
                cb[y * w + x] = b;
            }
        double Band(float[] img, double s0, double s1)
        {
            var b0 = s0 == 0 ? img : Blur(img, w, h, s0);
            var b1 = Blur(img, w, h, s1);
            double e = 0;
            int n = 0;
            for (int y = 12; y < h - 12; y++)
                for (int x = 12; x < w - 12; x++)
                {
                    double d = b0[y * w + x] - b1[y * w + x];
                    e += d * d;
                    n++;
                }
            return Math.Sqrt(e / n);
        }
        double fine = Band(luma, 0.7, 1.4) * 100, medium = Band(luma, 1.4, 2.8) * 100;
        double chroma = Math.Sqrt(Math.Pow(Band(ca, 0, 1.0), 2) + Math.Pow(Band(cb, 0, 1.0), 2)) * 1000;
        return (fine, medium, chroma);
    }

    private static float[] Blur(float[] img, int w, int h, double sigma)
    {
        int r = (int)Math.Ceiling(sigma * 3);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(i => (float)Math.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        float ks = k.Sum();
        var tmp = new float[img.Length];
        var res = new float[img.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * img[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = s / ks;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++) s += k[i + r] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                res[y * w + x] = s / ks;
            }
        return res;
    }

    /// <summary>Shift of Lightroom's image against ours at this crop (normalised correlation of luma, ±40 px).</summary>
    private static (int, int) Align(SKBitmap lr, SKBitmap ours, SKRectI r)
    {
        float L(SKBitmap b, int x, int y) { var c = b.GetPixel(x, y); return 0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue; }
        var a = new List<float>();
        for (int y = r.Top; y < r.Bottom; y += 2)
            for (int x = r.Left; x < r.Right; x += 2)
                a.Add(L(ours, x, y));
        float am = a.Average();
        double best = double.MinValue;
        (int, int) at = (0, 0);
        var lock_ = new object();
        Parallel.For(-40, 41, dy =>
        {
            for (int dx = -40; dx <= 40; dx++)
            {
                double sab = 0, sbb = 0, sb = 0;
                int i = 0;
                var bv = new float[a.Count];
                for (int y = r.Top; y < r.Bottom; y += 2)
                    for (int x = r.Left; x < r.Right; x += 2)
                        bv[i++] = L(lr, x - dx, y - dy);
                float bm = bv.Average();
                for (int j = 0; j < bv.Length; j++)
                {
                    sab += (a[j] - am) * (bv[j] - bm);
                    sbb += (bv[j] - bm) * (bv[j] - bm);
                }
                double c = sab / Math.Sqrt(sbb + 1e-9);
                lock (lock_)
                    if (c > best) { best = c; at = (dx, dy); }
            }
        });
        return at;
    }

    private static void WriteGrid(List<(string Name, SKBitmap Bitmap, SKRectI Rect)> columns, string path)
    {
        const int label = 26, gap = 6;
        int cell = Crop * Zoom;
        int cols = Math.Min(columns.Count, 4), rows = (columns.Count + cols - 1) / cols;
        using var surface = SKSurface.Create(new SKImageInfo(cols * (cell + gap), rows * (cell + label + gap)));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(40, 40, 40));
        using var font = new SKFont(SKTypeface.Default, 20);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (int i = 0; i < columns.Count; i++)
        {
            var (name, bitmap, rect) = columns[i];
            int cx = i % cols * (cell + gap), cy = i / cols * (cell + label + gap);
            canvas.DrawText(name, cx + 6, cy + 20, font, paint);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, rect, new SKRect(cx, cy + label, cx + cell, cy + label + cell),
                new SKSamplingOptions(SKFilterMode.Nearest), null);
        }
        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
