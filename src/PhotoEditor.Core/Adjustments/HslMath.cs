namespace PhotoEditor.Core.Adjustments;

/// <summary>HSL panel math. Mirrored in <see cref="AdjustmentShader"/>.</summary>
public static class HslMath
{
    /// <summary>Applies the per-band hue/saturation/luminance adjustments to linear RGB.</summary>
    public static void Apply(ref float r, ref float g, ref float b, float[] hsl)
    {
        // Hue and saturation are judged on gamma-encoded values (closer to perception).
        const float gamma = ToneCurve.PerceptualGamma;
        var (h, s, v) = RgbToHsv(
            MathF.Pow(MathF.Max(r, 0f), 1f / gamma),
            MathF.Pow(MathF.Max(g, 0f), 1f / gamma),
            MathF.Pow(MathF.Max(b, 0f), 1f / gamma));

        var (hueShift, satFactor, lumStops) = Interpolate(h, hsl);
        float h2 = h + hueShift;
        if (h2 < 0f) h2 += 360f;
        if (h2 >= 360f) h2 -= 360f;
        float s2 = Math.Clamp(s * satFactor, 0f, 1f);
        var (er, eg, eb) = HsvToRgb(h2, s2, v);

        // Luminance change is scaled by saturation so greys stay untouched.
        float gain = MathF.Pow(2f, lumStops * s);
        r = MathF.Pow(er, gamma) * gain;
        g = MathF.Pow(eg, gamma) * gain;
        b = MathF.Pow(eb, gamma) * gain;
    }

    /// <summary>Blends the adjustments of the two bands around hue <paramref name="h"/> (degrees).</summary>
    public static (float HueShift, float SatFactor, float LumStops) Interpolate(float h, float[] hsl)
    {
        var centers = HslBands.Centers;
        int i = HslBands.Count - 1;
        for (int k = 0; k < HslBands.Count - 1; k++)
        {
            if (h < centers[k + 1])
            {
                i = k;
                break;
            }
        }
        int j = (i + 1) % HslBands.Count;
        float c0 = centers[i];
        float c1 = i == HslBands.Count - 1 ? 360f : centers[i + 1];
        float t = ToneCurve.SmoothStep(0f, 1f, (h - c0) / (c1 - c0));
        return (
            Lerp(hsl[i * 3], hsl[j * 3], t),
            Lerp(hsl[i * 3 + 1], hsl[j * 3 + 1], t),
            Lerp(hsl[i * 3 + 2], hsl[j * 3 + 2], t));
    }

    /// <summary>RGB → hue (0..360), saturation (0..1), value.</summary>
    public static (float H, float S, float V) RgbToHsv(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        float s = max > 0f ? d / max : 0f;
        if (d <= 0f)
            return (0f, s, max);
        float h;
        if (max == r)
            h = 60f * ((g - b) / d);
        else if (max == g)
            h = 60f * ((b - r) / d + 2f);
        else
            h = 60f * ((r - g) / d + 4f);
        if (h < 0f)
            h += 360f;
        return (h, s, max);
    }

    public static (float R, float G, float B) HsvToRgb(float h, float s, float v) =>
        (HsvChannel(5f, h, s, v), HsvChannel(3f, h, s, v), HsvChannel(1f, h, s, v));

    private static float HsvChannel(float n, float h, float s, float v)
    {
        float k = (n + h / 60f) % 6f;
        return v - v * s * MathF.Max(0f, MathF.Min(k, MathF.Min(4f - k, 1f)));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
