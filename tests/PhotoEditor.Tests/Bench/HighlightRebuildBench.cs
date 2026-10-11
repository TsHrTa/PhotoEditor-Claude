using System.Globalization;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// Where the sensor clips in a region of a RAW and what the highlight rebuild makes of it: per channel the share of
/// pixels near / at the clip level, the scene colour (linear sRGB, as ratios to green) before and after the rebuild by
/// brightness band, and Lightroom's mean colour per set of clipped channels (with a map of them next to Lightroom's
/// crop). Runs only when PHOTOEDITOR_BENCH is set; PHOTOEDITOR_LR_DIR / PHOTOEDITOR_LR_FILE (default: the
/// Highlights −95 export), PHOTOEDITOR_BENCH_OUT for the maps, PHOTOEDITOR_LR_REGIONS as in
/// <see cref="LightroomCompareBench"/> (default: the R8 sample's sky crop).
/// </summary>
public sealed class HighlightRebuildBench(ITestOutputHelper output)
{
    [Fact]
    public void Measure()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        if (raw is null)
            return;
        var regions = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_REGIONS") ?? "3150,1800,500";
        var (rgb, w, h, clip, matrix) = RawImageLoader.LoadSensor(raw, RawDemosaic.Dcb);
        var rebuilt = RawImageLoader.ReconstructAll(rgb, w, h, clip);
        var outDir = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH_OUT");
        var lrDir = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_DIR");
        var lrFile = Environment.GetEnvironmentVariable("PHOTOEDITOR_LR_FILE")
            ?? "IMG_5645 sharpening 40 saturation 65 vibrance 40 exposure 1 highlights -95";
        using var lr = lrDir is null ? null : TiffImageLoader.Load(Path.Combine(lrDir, lrFile + ".tif"));
        if (outDir is not null)
            Directory.CreateDirectory(outDir);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{w} x {h}, clip {clip[0]:F0} {clip[1]:F0} {clip[2]:F0}, matrix {string.Join(" ", matrix.Select(m => m.ToString("F3", CultureInfo.InvariantCulture)))}"));
        foreach (var part in regions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = part.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var r = new SKRectI(v[0], v[1], v[0] + v[2], v[1] + v[2]);
            output.WriteLine($"region {r}");
            var near = new int[3];
            var at = new int[3];
            int n = 0;
            var classN = new int[8];
            var classLr = new (double R, double G, double B)[8];
            var classOurs = new (double R, double G, double B)[8];
            using var map = outDir is null ? null : new SKBitmap(r.Width, r.Height);
            // bands by the brightest channel / its clip
            var bands = new (double R, double G, double B, double Rr, double Gr, double Br, int N)[8];
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    int i = (y * w + x) * 3;
                    double top = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        double f = rgb[i + c] / clip[c];
                        if (f >= 0.8) near[c]++;
                        if (f >= 0.97) at[c]++;
                        top = Math.Max(top, f);
                    }
                    n++;
                    int band = Math.Clamp((int)((top - 0.3) / 0.1), 0, 7);
                    var s = Scene(rgb[i], rgb[i + 1], rgb[i + 2], matrix, clip);
                    var t = Scene(rebuilt[i], rebuilt[i + 1], rebuilt[i + 2], matrix, clip);
                    var b = bands[band];
                    bands[band] = (b.R + s[0], b.G + s[1], b.B + s[2], b.Rr + t[0], b.Gr + t[1], b.Br + t[2], b.N + 1);
                    int cls = (rgb[i] >= 0.97f * clip[0] ? 1 : 0) | (rgb[i + 1] >= 0.97f * clip[1] ? 2 : 0)
                        | (rgb[i + 2] >= 0.97f * clip[2] ? 4 : 0);
                    classN[cls]++;
                    classOurs[cls] = (classOurs[cls].R + t[0], classOurs[cls].G + t[1], classOurs[cls].B + t[2]);
                    if (lr is not null)
                    {
                        var c = lr.GetPixel(x, y);
                        classLr[cls] = (classLr[cls].R + c.Red, classLr[cls].G + c.Green, classLr[cls].B + c.Blue);
                    }
                    map?.SetPixel(x - r.Left, y - r.Top, ClassColours[cls]);
                }
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  >= 0.8 clip: R {100.0 * near[0] / n:F1} % G {100.0 * near[1] / n:F1} % B {100.0 * near[2] / n:F1} %; >= 0.97: R {100.0 * at[0] / n:F1} % G {100.0 * at[1] / n:F1} % B {100.0 * at[2] / n:F1} %"));
            output.WriteLine("  max channel / clip band: n, scene sRGB as-is (R/G, B/G, G), rebuilt (R/G, B/G, G)");
            for (int k = 0; k < 8; k++)
            {
                var b = bands[k];
                if (b.N == 0)
                    continue;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {0.3 + k * 0.1:F1}: {b.N,7}  ({b.R / b.G:F2}, {b.B / b.G:F2}, {b.G / b.N:F2})  ({b.Rr / b.Gr:F2}, {b.Br / b.Gr:F2}, {b.Gr / b.N:F2})"));
            }
            output.WriteLine("  clipped channels (>= 0.97): n, Lightroom's mean rgb, our rebuilt scene (R/G, B/G, G)");
            for (int k = 0; k < 8; k++)
                if (classN[k] > 0)
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  {(k == 0 ? "none" : string.Concat((k & 1) != 0 ? "R" : "", (k & 2) != 0 ? "G" : "", (k & 4) != 0 ? "B" : "")),-5} {classN[k],7}  ({classLr[k].R / classN[k]:F0}, {classLr[k].G / classN[k]:F0}, {classLr[k].B / classN[k]:F0})  ({classOurs[k].R / classOurs[k].G:F2}, {classOurs[k].B / classOurs[k].G:F2}, {classOurs[k].G / classN[k]:F2})"));
            if (map is not null && lr is not null)
            {
                using var surface = SKSurface.Create(new SKImageInfo(r.Width * 2 + 6, r.Height));
                surface.Canvas.Clear(SKColors.Black);
                using (var a = SKImage.FromBitmap(lr)) surface.Canvas.DrawImage(a, r, new SKRect(0, 0, r.Width, r.Height));
                surface.Canvas.DrawBitmap(map, r.Width + 6, 0);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(outDir!, $"clipmap_{r.Left}_{r.Top}_{r.Width}.png"), data.ToArray());
            }
        }
    }

    /// <summary>Clip classes (bit 0 R, 1 G, 2 B) for the map: none black, G green, B blue, G+B cyan, all white.</summary>
    private static readonly SKColor[] ClassColours =
    [
        SKColors.Black, SKColors.Red, SKColors.Lime, SKColors.Yellow, SKColors.Blue, SKColors.Magenta, SKColors.Cyan, SKColors.White,
    ];

    private static float[] Scene(float r, float g, float b, float[] m, float[] clip)
    {
        float white = clip.Min();
        return [(m[0] * r + m[1] * g + m[2] * b) / white, (m[3] * r + m[4] * g + m[5] * b) / white, (m[6] * r + m[7] * g + m[8] * b) / white];
    }
}
