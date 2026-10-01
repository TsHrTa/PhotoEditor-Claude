namespace PhotoEditor.Core.Adjustments;

/// <summary>HSL panel math. Mirrored in <see cref="AdjustmentShader"/>.</summary>
public static class HslMath
{
    /// <summary>
    /// Applies the per-band hue / saturation / luminance adjustments to linear RGB, in OKLCh: a hue move keeps the colour's
    /// lightness (HSV on gamma-encoded values made blue to cyan visibly brighter), saturation scales the chroma, and a
    /// colour pushed outside sRGB loses chroma instead of a channel being clipped. Luminance (stops) is weighted by
    /// chroma, so greys are untouched.
    /// </summary>
    public static void Apply(ref float r, ref float g, ref float b, float[] hsl)
    {
        var (l, a, bb) = ToOklab(r, g, b);
        float chroma = MathF.Sqrt(a * a + bb * bb);
        if (chroma < 1e-5f)
            return; // greys have no hue: nothing to adjust
        float hue = MathF.Atan2(bb, a) * (180f / MathF.PI);
        if (hue < 0f) hue += 360f;

        var (hueShift, satFactor, lumStops) = Interpolate(hue, hsl);
        // Hue shift = a rotation of (a, b), saturation = a scale of it (as in the shader).
        float theta = hueShift * (MathF.PI / 180f), ct = MathF.Cos(theta), st = MathF.Sin(theta);
        float a2 = satFactor * (a * ct - bb * st), b2 = satFactor * (a * st + bb * ct);
        // A colour outside sRGB loses chroma at the same lightness and hue (see GamutMapping).
        (r, g, b) = GamutMapping.FromOklab(l, a2, b2);
        float gain = MathF.Pow(2f, lumStops * Math.Clamp(chroma / 0.15f, 0f, 1f));
        r *= gain;
        g *= gain;
        b *= gain;
    }

    /// <summary>Linear sRGB to OKLab (Bjorn Ottosson). Negative channels count as 0.</summary>
    public static (float L, float A, float B) ToOklab(float r, float g, float b)
    {
        float l = MathF.Cbrt(MathF.Max(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b, 0f));
        float m = MathF.Cbrt(MathF.Max(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b, 0f));
        float s = MathF.Cbrt(MathF.Max(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b, 0f));
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    /// <summary>OKLab to linear sRGB.</summary>
    public static (float R, float G, float B) FromOklab(float lightness, float a, float b)
    {
        float l = lightness + 0.3963377774f * a + 0.2158037573f * b;
        float m = lightness - 0.1055613458f * a - 0.0638541728f * b;
        float s = lightness - 0.0894841775f * a - 1.2914855480f * b;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
                -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
                -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    /// <summary>OKLCh of a linear colour: lightness, chroma, hue (degrees, 0 to 360).</summary>
    public static (float Lightness, float Chroma, float Hue) Oklch(float r, float g, float b)
    {
        var (l, a, bb) = ToOklab(r, g, b);
        float hue = MathF.Atan2(bb, a) * (180f / MathF.PI);
        return (l, MathF.Sqrt(a * a + bb * bb), hue < 0f ? hue + 360f : hue);
    }

    /// <summary>
    /// Blends the adjustments of the two bands around OKLCh hue <paramref name="h"/> (degrees); the band centres wrap
    /// around from magenta to red.
    /// </summary>
    public static (float HueShift, float SatFactor, float LumStops) Interpolate(float h, float[] hsl)
    {
        var centers = HslBands.RelativeCenters;
        h = ((h - HslBands.Centers[0]) % 360f + 360f) % 360f;
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
