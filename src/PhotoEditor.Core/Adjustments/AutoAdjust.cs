using PhotoEditor.Core.Editing;
using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// "Auto" button: suggests Light sliders, white balance and vibrance from the photo's statistics
/// (brightness distribution and average colour). Classic, non-AI heuristics; the result is ordinary
/// slider values that can be fine-tuned.
/// </summary>
/// <remarks>
/// Always analyses the unedited pixels (inside the crop), so pressing Auto twice gives the same result.
/// Each step is checked by running the samples through the real pipeline (<see cref="CpuAdjustmentRenderer.ApplyLinear"/>).
/// Perceptual values are luminance^(1/2.2): 0 = black, ~0.46 = middle grey, 1 = white.
/// </remarks>
public static class AutoAdjust
{
    /// <summary>Samples along each axis of the crop (≈ 62 500 in total).</summary>
    public const int SampleGrid = 250;

    private const float TargetMedian = 0.45f;
    private const float TargetWhite = 0.97f;
    private const float TargetBlack = 0.02f;

    /// <summary>
    /// Returns <paramref name="current"/> with Exposure, Contrast, Highlights, Shadows, Whites, Blacks,
    /// Temperature, Tint and Vibrance replaced by suggestions for <paramref name="image"/> within <paramref name="crop"/>.
    /// HSL, saturation and vignette are kept.
    /// </summary>
    public static AdjustmentSettings Suggest(SKBitmap image, Crop crop, AdjustmentSettings current)
    {
        var samples = Sample(image, crop);
        if (samples.Length == 0)
            return current;

        var s = current with
        {
            Exposure = 0, Contrast = 0, Highlights = 0, Shadows = 0, Whites = 0, Blacks = 0,
            Temperature = 0, Tint = 0, Vibrance = 0, Saturation = 0,
        };

        // 1. White balance: pull the average colour of the mid-tones half way towards neutral (grey world).
        var (temperature, tint) = WhiteBalance(samples);
        s = Set(s, AdjustmentParameters.Temperature, temperature);
        s = Set(s, AdjustmentParameters.Tint, tint);

        // 2. Exposure: bring the median brightness towards middle grey. Large corrections are softened
        //    (+4 EV needed → about +2) so night shots and bright scenes keep some of their character.
        float median = Percentile(Perceptual(samples, s), 0.5f);
        double needed = median > 1e-4f ? PerceptualGamma * Math.Log2(TargetMedian / median) : 5;
        s = Set(s, AdjustmentParameters.Exposure, Math.Clamp(SoftLimit(needed, 1.5), -2.5, 2.5));

        // 3. Contrast from the spread of the mid-range; highlights / shadows from clipping and dark areas.
        var v = Perceptual(samples, s);
        float spread = Percentile(v, 0.9f) - Percentile(v, 0.1f);
        s = Set(s, AdjustmentParameters.Contrast, Math.Clamp((0.6 - spread) * 150, -20, 60));
        float clipped = Fraction(v, x => x >= 0.97f);
        float p99 = Percentile(v, 0.99f);
        double highlights = -Math.Max(clipped * 600, (p99 - 0.9) * 250);
        s = Set(s, AdjustmentParameters.Highlights, Math.Clamp(highlights, -80, 0));
        float dark = Fraction(v, x => x < 0.12f);
        s = Set(s, AdjustmentParameters.Shadows, Math.Clamp(dark * 150, 0, 50));

        // 4. Whites / blacks: stretch the ends of the range to near white / black (two refinement passes).
        for (int pass = 0; pass < 2; pass++)
        {
            v = Perceptual(samples, s);
            float hi = Percentile(v, 0.995f), lo = Percentile(v, 0.005f);
            double whites = s.Whites + (TargetWhite - hi) / (0.15 * Math.Pow(Math.Max(hi, 0.3f), 4)) * 100 * (pass == 0 ? 1 : 0.5);
            double blacks = s.Blacks + (TargetBlack - lo) / (0.15 * Math.Pow(1 - Math.Min(lo, 0.7f), 4)) * 100 * (pass == 0 ? 1 : 0.5);
            s = Set(s, AdjustmentParameters.Whites, Math.Clamp(whites, -50, 50));
            s = Set(s, AdjustmentParameters.Blacks, Math.Clamp(blacks, -50, 30));
        }

        // 5. Vibrance for muted photos.
        float saturation = MeanSaturation(samples, s);
        s = Set(s, AdjustmentParameters.Vibrance, Math.Clamp((0.4 - saturation) * 60, 0, 25));

        return s with { Saturation = current.Saturation };
    }

    private const float PerceptualGamma = ToneCurve.PerceptualGamma;

    /// <summary>≈ x for small values, grows logarithmically beyond <paramref name="knee"/>.</summary>
    public static double SoftLimit(double x, double knee) => Math.Sign(x) * knee * Math.Log(1 + Math.Abs(x) / knee);

    private static AdjustmentSettings Set(AdjustmentSettings s, AdjustmentParameter p, double value) => p.Set(s, value);

    /// <summary>Linear RGB of a grid of pixels inside the crop frame (transparent pixels skipped).</summary>
    public static (float R, float G, float B)[] Sample(SKBitmap image, Crop crop)
    {
        using var converted = image.ColorType == SKColorType.Rgba8888 ? null : image.Copy(SKColorType.Rgba8888);
        var bitmap = converted ?? image;
        var pixels = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes, w = bitmap.Width, h = bitmap.Height;
        var f = crop.Frame(w, h);
        var result = new List<(float, float, float)>(SampleGrid * SampleGrid);
        for (int j = 0; j < SampleGrid; j++)
        {
            double v = ((j + 0.5) / SampleGrid * 2 - 1) * f.HalfHeight;
            for (int i = 0; i < SampleGrid; i++)
            {
                double u = ((i + 0.5) / SampleGrid * 2 - 1) * f.HalfWidth;
                var (x, y) = f.ToImage(u, v);
                int px = Math.Clamp((int)x, 0, w - 1), py = Math.Clamp((int)y, 0, h - 1);
                int o = py * rowBytes + px * 4;
                byte a = pixels[o + 3];
                if (a == 0)
                    continue;
                float inv = 255f / a;
                result.Add((
                    ColorMath.SrgbToLinear(Math.Min(1f, pixels[o] * inv / 255f)),
                    ColorMath.SrgbToLinear(Math.Min(1f, pixels[o + 1] * inv / 255f)),
                    ColorMath.SrgbToLinear(Math.Min(1f, pixels[o + 2] * inv / 255f))));
            }
        }
        return [.. result];
    }

    /// <summary>Temperature / tint (slider units) that move the mid-tone average half way to neutral.</summary>
    private static (double Temperature, double Tint) WhiteBalance((float R, float G, float B)[] samples)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        foreach (var (sr, sg, sb) in samples)
        {
            float y = ToneCurve.Luminance(sr, sg, sb);
            float max = MathF.Max(sr, MathF.Max(sg, sb));
            // Mid-tones only: shadows are noisy, clipped highlights have lost their colour.
            if (y < 0.02f || max > 0.95f)
                continue;
            r += sr; g += sg; b += sb; n++;
        }
        if (n < samples.Length / 20 || r <= 0 || g <= 0 || b <= 0)
            return (0, 0);
        const double strength = 0.5;
        // WhiteBalanceGains: red/blue = exp(0.7 t), green / sqrt(red·blue) = exp(-0.25 n), t and n in -1..1.
        double temperature = Math.Log(b / r) / 0.7 * 100 * strength;
        double tint = -Math.Log(Math.Sqrt(r * b) / g) / 0.25 * 100 * strength;
        return (Math.Clamp(temperature, -40, 40), Math.Clamp(tint, -30, 30));
    }

    /// <summary>Perceptual brightness of each sample after <paramref name="s"/>, sorted ascending.</summary>
    public static float[] Perceptual((float R, float G, float B)[] samples, AdjustmentSettings s)
    {
        var p = PreparedAdjustments.From(s);
        var values = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            var (r, g, b) = samples[i];
            CpuAdjustmentRenderer.ApplyLinear(ref r, ref g, ref b, p);
            float y = ToneCurve.Luminance(Math.Min(r, 1f), Math.Min(g, 1f), Math.Min(b, 1f));
            values[i] = MathF.Pow(MathF.Max(y, 0f), 1f / PerceptualGamma);
        }
        Array.Sort(values);
        return values;
    }

    private static float Percentile(float[] sorted, float q) =>
        sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];

    private static float Fraction(float[] values, Func<float, bool> predicate) =>
        (float)values.Count(predicate) / values.Length;

    private static float MeanSaturation((float R, float G, float B)[] samples, AdjustmentSettings s)
    {
        var p = PreparedAdjustments.From(s);
        double sum = 0;
        foreach (var (sr, sg, sb) in samples)
        {
            float r = sr, g = sg, b = sb;
            CpuAdjustmentRenderer.ApplyLinear(ref r, ref g, ref b, p);
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            sum += max > 1e-4f ? (max - min) / max : 0;
        }
        return (float)(sum / samples.Length);
    }
}
