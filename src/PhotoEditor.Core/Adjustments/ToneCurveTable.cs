namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// The tone curve panel (parametric regions, then the RGB point curve, then the red / green / blue point curves) as
/// one lookup table per channel on perceptual values (linear^(1/2.2), 0..1). Used by both renderers: the CPU
/// interpolates it (<see cref="Apply"/>), the shader samples it as a <see cref="Size"/> × 1 image with linear
/// filtering, which is the same interpolation.
/// </summary>
public static class ToneCurveTable
{
    /// <summary>Entries per channel.</summary>
    public const int Size = 1024;

    /// <summary>Largest move (perceptual units) of a parametric region slider at ±100, at the region's middle.</summary>
    public const double ParametricStrength = 0.12;

    private sealed record Key(double Highlights, double Lights, double Darks, double Shadows,
        double ShadowSplit, double MidtoneSplit, double HighlightSplit, PointCurve Rgb, PointCurve Red, PointCurve Green, PointCurve Blue);

    private static (Key Key, float[] Table)? _last;
    private static readonly object Lock = new();

    /// <summary>True when the settings' tone curve changes nothing.</summary>
    public static bool IsIdentity(AdjustmentSettings s) =>
        s.CurveHighlights == 0 && s.CurveLights == 0 && s.CurveDarks == 0 && s.CurveShadows == 0
        && s.Curve.IsLinear && s.CurveRed.IsLinear && s.CurveGreen.IsLinear && s.CurveBlue.IsLinear;

    /// <summary>
    /// The table (<see cref="Size"/> entries of red, green, blue) for the settings, or null when the curve changes
    /// nothing. The same array is returned for the same curve (so the shader's table image is made once).
    /// </summary>
    public static float[]? For(AdjustmentSettings s)
    {
        if (IsIdentity(s))
            return null;
        var key = new Key(s.CurveHighlights, s.CurveLights, s.CurveDarks, s.CurveShadows, s.CurveShadowSplit, s.CurveMidtoneSplit,
            s.CurveHighlightSplit, s.Curve, s.CurveRed, s.CurveGreen, s.CurveBlue);
        lock (Lock)
        {
            if (_last is { } last && last.Key == key)
                return last.Table;
        }
        var table = Build(s);
        lock (Lock)
            _last = (key, table);
        return table;
    }

    private static float[] Build(AdjustmentSettings s)
    {
        var parametric = Parametric(s);
        var channels = new[] { s.CurveRed, s.CurveGreen, s.CurveBlue };
        var table = new float[Size * 3];
        for (int i = 0; i < Size; i++)
        {
            double v = s.Curve.Evaluate(parametric[i]);
            for (int c = 0; c < 3; c++)
                table[i * 3 + c] = (float)channels[c].Evaluate(v);
        }
        return table;
    }

    /// <summary>
    /// The parametric curve sampled at the table's points: each region slider lifts or lowers a smooth bump over its
    /// region and half of each neighbour (the ends 0 and 1 stay), kept rising. An approximation of Lightroom's
    /// curve, which is not documented.
    /// </summary>
    public static double[] Parametric(AdjustmentSettings s)
    {
        double s1 = Math.Clamp(s.CurveShadowSplit / 100, 0.05, 0.85);
        // Each split stays 5 % above the one before (Math.Max: 0.85 + 0.05 + 0.05 rounds just above 0.95).
        double s2 = Math.Clamp(s.CurveMidtoneSplit / 100, s1 + 0.05, Math.Max(s1 + 0.05, 0.9));
        double s3 = Math.Clamp(s.CurveHighlightSplit / 100, s2 + 0.05, Math.Max(s2 + 0.05, 0.95));
        double[] bounds = [0, s1, s2, s3, 1];
        double[] amounts = [s.CurveShadows, s.CurveDarks, s.CurveLights, s.CurveHighlights];
        var values = new double[Size];
        double previous = 0;
        for (int i = 0; i < Size; i++)
        {
            double x = i / (double)(Size - 1);
            double y = x;
            for (int k = 0; k < 4; k++)
            {
                if (amounts[k] == 0)
                    continue;
                double lo = k == 0 ? 0 : (bounds[k - 1] + bounds[k]) / 2;
                double hi = k == 3 ? 1 : (bounds[k + 1] + bounds[k + 2]) / 2;
                if (x <= lo || x >= hi)
                    continue;
                double w = Math.Sin(Math.PI * (x - lo) / (hi - lo));
                y += Math.Clamp(amounts[k], -100, 100) / 100 * ParametricStrength * w * w;
            }
            y = Math.Clamp(y, 0, 1);
            if (i > 0)
                y = Math.Max(y, previous);
            values[i] = previous = y;
        }
        return values;
    }

    /// <summary>
    /// The curve applied to one perceptual value of <paramref name="channel"/> (0 red, 1 green, 2 blue). Values
    /// above 1 (RAW highlights) keep their distance above the curve's end. Mirrored in the shader.
    /// </summary>
    public static float Lookup(float[] table, int channel, float x)
    {
        float pos = Math.Clamp(x, 0f, 1f) * (Size - 1);
        int i = Math.Min((int)pos, Size - 2);
        float t = pos - i;
        float a = table[i * 3 + channel], b = table[(i + 1) * 3 + channel];
        return a + (b - a) * t + MathF.Max(x - 1f, 0f);
    }

    /// <summary>Applies the table to linear-light RGB (through perceptual values).</summary>
    public static void Apply(float[] table, ref float r, ref float g, ref float b)
    {
        const float inv = 1f / ToneCurve.PerceptualGamma;
        r = MathF.Pow(Lookup(table, 0, MathF.Pow(MathF.Max(r, 0f), inv)), ToneCurve.PerceptualGamma);
        g = MathF.Pow(Lookup(table, 1, MathF.Pow(MathF.Max(g, 0f), inv)), ToneCurve.PerceptualGamma);
        b = MathF.Pow(Lookup(table, 2, MathF.Pow(MathF.Max(b, 0f), inv)), ToneCurve.PerceptualGamma);
    }
}
