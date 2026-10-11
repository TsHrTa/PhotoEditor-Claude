using System.Globalization;
using PhotoEditor.Core.Imaging;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>
/// Where the red and blue images of a RAW sit relative to the green one, patch by patch over a grid (sub-pixel shift
/// with the best correlation of fine detail, measured as <see cref="Core.Lens.ChromaticAberration"/> does). Lateral
/// chromatic aberration shows as shifts along the radius growing with the distance from the centre; a shift that is
/// the same everywhere is not the lens. Runs only when PHOTOEDITOR_BENCH (the RAW) is set. Measures LibRaw's output
/// (PHOTOEDITOR_SHIFT_DEMOSAIC picks the demosaicing, default DCB), or with PHOTOEDITOR_SHIFT_IMAGE: "developed" (our
/// photo as the lens corrections get it, with what the measurement finds), "corrected" (the same after the
/// chromatic aberration removal: what is left), or the path of a rendered image (Lightroom's TIFF).
/// </summary>
public sealed class ChannelShiftBench(ITestOutputHelper output)
{
    [Fact]
    public void Measure()
    {
        var raw = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        if (raw is null)
            return;
        var demosaic = Enum.TryParse<RawDemosaic>(Environment.GetEnvironmentVariable("PHOTOEDITOR_SHIFT_DEMOSAIC"), true, out var d)
            ? d : RawImageLoader.DefaultDemosaic;
        var planes = new float[3][];
        int w, h;
        var mode = Environment.GetEnvironmentVariable("PHOTOEDITOR_SHIFT_IMAGE");
        if (mode is not null)
        {
            // A rendered image instead: "developed" = our photo as the lens corrections get it, "corrected" = the same
            // after the chromatic aberration removal, else a file (Lightroom's TIFF, our export); 8-bit values as they are.
            SkiaSharp.SKBitmap bitmap;
            if (mode is "developed" or "corrected")
            {
                bitmap = RawImageLoader.Load(raw);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var shift = Core.Lens.ChromaticAberration.Measure(bitmap);
                output.WriteLine($"measured in {watch.ElapsedMilliseconds} ms");
                double hd = Math.Sqrt((double)bitmap.Width * bitmap.Width + (double)bitmap.Height * bitmap.Height) / 2;
                string Moves(Core.Lens.ChannelShift s, float fx, float fy) => s.Moves?.At(fx, fy) is { } m
                    ? string.Create(CultureInfo.InvariantCulture, $"red ({m.RedX * hd:F2},{m.RedY * hd:F2}) blue ({m.BlueX * hd:F2},{m.BlueY * hd:F2})")
                    : "none";
                output.WriteLine(shift is { } s
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"measured: red scale {(s.RedScale - 1) * hd:F2} px at the corner, blue {(s.BlueScale - 1) * hd:F2}; moves at the centre {Moves(s, 0.5f, 0.5f)}, top {Moves(s, 0.5f, 0.1f)}, bottom {Moves(s, 0.5f, 0.9f)}")
                    : "measured: nothing");
                if (mode == "corrected")
                {
                    var settings = Core.Adjustments.AdjustmentSettings.RawDefault with { RemoveChromaticAberration = true, LensProfile = false };
                    var correction = Core.Lens.LensSetup.For(Core.Lens.PhotoLens.Of(raw), settings, bitmap.Width, bitmap.Height,
                        measureCa: () => Core.Lens.ChromaticAberration.Measure(bitmap));
                    if (correction is not null)
                    {
                        var corrected = correction.Apply(bitmap);
                        if (!ReferenceEquals(corrected, bitmap))
                        {
                            bitmap.Dispose();
                            bitmap = corrected;
                        }
                    }
                }
            }
            else
                bitmap = TiffImageLoader.IsTiff(mode) ? TiffImageLoader.Load(mode) : SkiaSharp.SKBitmap.Decode(mode);
            using var _ = bitmap;
            var rendered = mode;
            (w, h) = (bitmap.Width, bitmap.Height);
            for (int c = 0; c < 3; c++)
            {
                var p = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var color = bitmap.GetPixel(x, y);
                        p[y * w + x] = (c == 0 ? color.Red : c == 1 ? color.Green : color.Blue) / 255f;
                    }
                planes[c] = HighPass(p, w, h);
            }
            output.WriteLine($"{w} x {h}, {Path.GetFileName(rendered)}");
        }
        else
        {
            var (rgb, rw, rh, _, _) = RawImageLoader.LoadSensor(raw, demosaic);
            (w, h) = (rw, rh);
            for (int c = 0; c < 3; c++)
            {
                var p = new float[w * h];
                for (int i = 0; i < p.Length; i++)
                    p[i] = MathF.Sqrt(rgb[i * 3 + c] / 65535f); // gamma-ish so dark edges count too
                planes[c] = HighPass(p, w, h);
            }
            output.WriteLine($"{w} x {h}, {demosaic}");
        }
        const int grid = 7, patch = 300;
        double cx = w / 2.0, cy = h / 2.0, half = Math.Sqrt(cx * cx + cy * cy);
        var lines = new string[grid * grid];
        Parallel.For(0, grid * grid, index =>
            {
                int gx = index % grid, gy = index / grid;
                int x0 = (int)((gx + 0.5) * w / grid) - patch / 2, y0 = (int)((gy + 0.5) * h / grid) - patch / 2;
                var (rx, ry, rs) = BestShift(planes[0], planes[1], w, h, x0, y0, patch);
                var (bx, by, bs) = BestShift(planes[2], planes[1], w, h, x0, y0, patch);
                double dx = x0 + patch / 2.0 - cx, dy = y0 + patch / 2.0 - cy, r = Math.Sqrt(dx * dx + dy * dy);
                double rr = r > 0 ? (rx * dx + ry * dy) / r : 0, br = r > 0 ? (bx * dx + by * dy) / r : 0;
                lines[index] = string.Create(CultureInfo.InvariantCulture,
                    $"({x0 + patch / 2,4},{y0 + patch / 2,4}) r {r / half:F2}: red ({rx,5:F2},{ry,5:F2}) radial {rr,5:F2} [{rs:F2}]  blue ({bx,5:F2},{by,5:F2}) radial {br,5:F2} [{bs:F2}]");
            });
        foreach (var line in lines)
            output.WriteLine(line);
    }

    /// <summary>Shift (sx, sy) such that channel(p + s) best matches green(p) in the patch, and the correlation.</summary>
    private static (double X, double Y, double Score) BestShift(float[] channel, float[] green, int w, int h, int x0, int y0, int size)
    {
        double bestScore = double.MinValue, bx = 0, by = 0;
        // Coarse to fine: ±3 px in half pixels, then around the best in 1/8 and 1/32 pixel steps.
        foreach (var (step, range) in new[] { (0.5, 3.0), (0.125, 0.5), (0.03125, 0.125) })
        {
            double ox = bx, oy = by;
            for (double sy = oy - range; sy <= oy + range + 1e-9; sy += step)
            for (double sx = ox - range; sx <= ox + range + 1e-9; sx += step)
            {
                double cg = 0, cc = 0, gg = 0;
                for (int y = y0; y < y0 + size; y++)
                    for (int x = x0; x < x0 + size; x++)
                    {
                        float g = Bilinear(green, w, h, x - sx / 2, y - sy / 2); // both halfway: no bias to whole pixels
                        float c = Bilinear(channel, w, h, x + sx / 2, y + sy / 2);
                        cg += c * g;
                        cc += c * c;
                        gg += g * g;
                    }
                double score = cc > 0 && gg > 0 ? cg / Math.Sqrt(cc * gg) : 0;
                if (score > bestScore)
                    (bestScore, bx, by) = (score, sx, sy);
            }
        }
        return (bx, by, bestScore);
    }

    /// <summary>Detail between about 2 and 5 pixels (binomial-smoothed, minus its 5 × 5 average), as the measurement uses.</summary>
    private static float[] HighPass(float[] values, int w, int h)
    {
        var across = new float[values.Length];
        var smooth = new float[values.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 2; x < w - 2; x++)
            {
                int i = y * w + x;
                across[i] = (values[i - 2] + 4 * values[i - 1] + 6 * values[i] + 4 * values[i + 1] + values[i + 2]) / 16f;
            }
        });
        Parallel.For(2, h - 2, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                smooth[i] = (across[i - 2 * w] + 4 * across[i - w] + 6 * across[i] + 4 * across[i + w] + across[i + 2 * w]) / 16f;
            }
        });
        var result = new float[values.Length];
        Parallel.For(4, h - 4, y =>
        {
            for (int x = 4; x < w - 4; x++)
            {
                float sum = 0;
                for (int j = -2; j <= 2; j++)
                    for (int i = -2; i <= 2; i++)
                        sum += smooth[(y + j) * w + x + i];
                result[y * w + x] = smooth[y * w + x] - sum / 25f;
            }
        });
        return result;
    }

    private static float Bilinear(float[] v, int w, int h, double x, double y)
    {
        int x0 = Math.Clamp((int)Math.Floor(x), 0, w - 2), y0 = Math.Clamp((int)Math.Floor(y), 0, h - 2);
        float fx = (float)Math.Clamp(x - x0, 0, 1), fy = (float)Math.Clamp(y - y0, 0, 1);
        float top = v[y0 * w + x0] * (1 - fx) + v[y0 * w + x0 + 1] * fx;
        float bottom = v[(y0 + 1) * w + x0] * (1 - fx) + v[(y0 + 1) * w + x0 + 1] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}
