namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Saturation / Vibrance as a scaling of a linear colour's distance from its luminance. A boost is applied in full up
/// to the factor at which the smallest channel reaches 0 (the sRGB edge); beyond it the boost continues at
/// <see cref="Overshoot"/> of its strength, so saturated colours (blue sky, red flowers) still get more vivid with the
/// slider, while the slope change avoids a hard stop. The smallest channel then clips at 0, shifting the hue a little.
/// Mirrored in the shader (<c>limitSaturation</c>).
/// </summary>
public static class SaturationMath
{
    /// <summary>Share of the edge factor up to which a boost is applied in full.</summary>
    public const float Knee = 1f;

    /// <summary>Share of a boost that is still applied beyond the gamut edge.</summary>
    public const float Overshoot = 0.7f;

    /// <summary>
    /// The factor actually applied: <paramref name="factor"/> up to the factor at which the smallest channel reaches 0,
    /// then <see cref="Overshoot"/> of the rest. Never changes a factor ≤ 1.
    /// </summary>
    public static float LimitFactor(float factor, float luminance, float minChannel)
    {
        if (factor <= 1f || minChannel >= luminance || luminance <= 0f)
            return factor;
        float edge = MathF.Max(MathF.Max(luminance / (luminance - minChannel), 1f) * Knee, 1f);
        return factor <= edge ? factor : edge + Overshoot * (factor - edge);
    }

    public static void Apply(ref float r, ref float g, ref float b, float factor)
    {
        float y = ToneCurve.Luminance(r, g, b);
        factor = LimitFactor(factor, y, MathF.Min(r, MathF.Min(g, b)));
        r = MathF.Max(y + (r - y) * factor, 0f);
        g = MathF.Max(y + (g - y) * factor, 0f);
        b = MathF.Max(y + (b - y) * factor, 0f);
    }
}
