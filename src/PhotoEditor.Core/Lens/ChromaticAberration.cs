using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Lens;

/// <summary>
/// Measures lateral chromatic aberration in a photo: how much larger or smaller the red and blue images are than
/// the green one (radial scales around the centre). Strong edges away from the centre are compared: for each
/// candidate scale, the red (blue) fine detail at the scaled positions is correlated with the green detail; the
/// best-matching scale wins. Measured on a copy of fixed size, so the preview and the full-size photo give the
/// same result.
/// </summary>
public static class ChromaticAberration
{
    /// <summary>Long side of the copy the measurement uses.</summary>
    public const int MeasureSize = 1600;

    /// <summary>Largest scale difference considered (±1 %).</summary>
    public const double MaxDeviation = 0.01;

    private const double Step = 0.00025;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, Tuple<double, double>> Cache = new();

    /// <summary>Red and blue scales (1 = no aberration) of <paramref name="photo"/>, measured once per bitmap.</summary>
    public static (double Red, double Blue) Measure(SKBitmap photo)
    {
        var result = Cache.GetValue(photo, p =>
        {
            var (r, b) = MeasureCore(p);
            return Tuple.Create(r, b);
        });
        return (result.Item1, result.Item2);
    }

    private static (double Red, double Blue) MeasureCore(SKBitmap photo)
    {
        using var converted = photo.ColorType == SKColorType.Rgba8888 ? null : photo.Copy(SKColorType.Rgba8888);
        var source = converted ?? photo;
        var (w, h) = PreviewImage.PreviewSize(source.Width, source.Height, MeasureSize);
        using var small = PreviewImage.Downscale(source, w, h);
        var pixels = small.GetPixelSpan();
        int n = w * h;
        var red = new float[n];
        var green = new float[n];
        var blue = new float[n];
        for (int i = 0; i < n; i++)
        {
            red[i] = pixels[i * 4];
            green[i] = pixels[i * 4 + 1];
            blue[i] = pixels[i * 4 + 2];
        }
        var hr = HighPass(red, w, h);
        var hg = HighPass(green, w, h);
        var hb = HighPass(blue, w, h);

        // Edge samples: strong green gradients pointing roughly along the radius (only those show a radial shift),
        // in the outer part of the photo where the aberration is largest.
        double cx = w / 2.0, cy = h / 2.0, halfDiagonal = Math.Sqrt(cx * cx + cy * cy);
        var candidates = new List<(int X, int Y, float Strength)>();
        for (int y = 2; y < h - 2; y++)
            for (int x = 2; x < w - 2; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < 0.3 * halfDiagonal)
                    continue;
                float gx = green[y * w + x + 1] - green[y * w + x - 1];
                float gy = green[(y + 1) * w + x] - green[(y - 1) * w + x];
                float radial = (float)Math.Abs((gx * dx + gy * dy) / r);
                if (radial > 40)
                    candidates.Add((x, y, radial));
            }
        if (candidates.Count < 200)
            return (1, 1);
        var samples = candidates.OrderByDescending(c => c.Strength).Take(20000).ToList();
        return (BestScale(hr, hg, samples, w, h, cx, cy), BestScale(hb, hg, samples, w, h, cx, cy));
    }

    /// <summary>The scale at which <paramref name="channel"/> correlates best with green (1 if no clear winner).</summary>
    private static double BestScale(float[] channel, float[] green, List<(int X, int Y, float Strength)> samples, int w, int h, double cx, double cy)
    {
        int steps = (int)Math.Round(MaxDeviation / Step);
        var scores = new double[2 * steps + 1];
        for (int k = -steps; k <= steps; k++)
        {
            double s = 1 + k * Step;
            double cg = 0, cc = 0, gg = 0;
            foreach (var (x, y, _) in samples)
            {
                float g = green[y * w + x];
                float c = Bilinear(channel, w, h, cx + (x + 0.5 - cx) * s, cy + (y + 0.5 - cy) * s);
                cg += c * g;
                cc += c * c;
                gg += g * g;
            }
            scores[k + steps] = cc > 0 && gg > 0 ? cg / Math.Sqrt(cc * gg) : 0;
        }
        int best = Array.IndexOf(scores, scores.Max());
        // No clear peak (flat or at the edge of the range): leave the channel alone.
        if (best == 0 || best == scores.Length - 1 || scores[best] - scores[steps] < 0.002)
            return 1;
        // Parabola through the best score and its neighbours for a finer estimate.
        double a = scores[best - 1], b = scores[best], c2 = scores[best + 1];
        double denominator = a - 2 * b + c2;
        double offset = Math.Abs(denominator) > 1e-12 ? 0.5 * (a - c2) / denominator : 0;
        return 1 + (best - steps + Math.Clamp(offset, -0.5, 0.5)) * Step;
    }

    /// <summary>Pixel minus its 3 × 3 average (fine detail, so different channel levels don't matter).</summary>
    private static float[] HighPass(float[] values, int w, int h)
    {
        var result = new float[values.Length];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                float sum = 0;
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                        sum += values[(y + j) * w + x + i];
                result[y * w + x] = values[y * w + x] - sum / 9f;
            }
        return result;
    }

    private static float Bilinear(float[] v, int w, int h, double sx, double sy)
    {
        double u = sx - 0.5, t = sy - 0.5;
        int x0 = Math.Clamp((int)Math.Floor(u), 0, w - 2), y0 = Math.Clamp((int)Math.Floor(t), 0, h - 2);
        float fx = (float)Math.Clamp(u - x0, 0, 1), fy = (float)Math.Clamp(t - y0, 0, 1);
        float top = v[y0 * w + x0] * (1 - fx) + v[y0 * w + x0 + 1] * fx;
        float bottom = v[(y0 + 1) * w + x0] * (1 - fx) + v[(y0 + 1) * w + x0 + 1] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}
