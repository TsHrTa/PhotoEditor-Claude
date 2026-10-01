namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Brings a colour that sRGB cannot show into sRGB by reducing its chroma at constant lightness and hue (OKLab), rather
/// than clipping a channel or moving the colour towards its luminance in RGB (both change the hue: a strongly saturated
/// red turned orange by 14 degrees). Mirrored in the shader (<c>fitGamut</c>); the same fixed number of bisection steps on both sides.
/// </summary>
public static class GamutMapping
{
    /// <summary>Bisection steps: the chroma factor is found to 1/1024.</summary>
    public const int Steps = 10;

    /// <summary>
    /// How far below 0 (linear) a channel may be and still count as inside: the sRGB blue primary sits on the edge, and
    /// at constant OKLab hue the line from it towards grey leaves the gamut by up to 4e-4 before re-entering, so a
    /// zero tolerance would pull pure blues in by 10 %. The leftover is clipped (under 0.1 of an 8-bit level).
    /// </summary>
    public const float Tolerance = 5e-4f;

    /// <summary>The linear sRGB colour of OKLab (<paramref name="lightness"/>, <paramref name="a"/>, <paramref name="b"/>) with its chroma reduced until no channel is below 0.</summary>
    public static (float R, float G, float B) FromOklab(float lightness, float a, float b)
    {
        var rgb = HslMath.FromOklab(lightness, a, b);
        if (MathF.Min(rgb.R, MathF.Min(rgb.G, rgb.B)) >= -Tolerance)
            return (MathF.Max(rgb.R, 0f), MathF.Max(rgb.G, 0f), MathF.Max(rgb.B, 0f));
        float lo = 0f, hi = 1f;
        for (int i = 0; i < Steps; i++)
        {
            float mid = (lo + hi) * 0.5f;
            var t = HslMath.FromOklab(lightness, a * mid, b * mid);
            if (MathF.Min(t.R, MathF.Min(t.G, t.B)) >= -Tolerance)
                lo = mid;
            else
                hi = mid;
        }
        var fit = HslMath.FromOklab(lightness, a * lo, b * lo);
        return (MathF.Max(fit.R, 0f), MathF.Max(fit.G, 0f), MathF.Max(fit.B, 0f));
    }
}
