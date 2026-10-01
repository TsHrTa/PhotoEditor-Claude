namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Saturation / Vibrance as a scaling of a linear colour's distance from its luminance. Scaling up by a factor that
/// would push a channel below 0 (a colour that is already near the edge of sRGB) used to clip that channel, which
/// shifts the hue and flattens the colour; now the factor is limited softly so the colour stops at the gamut edge with
/// its hue and brightness kept. Mirrored in the shader (<c>saturate</c>).
/// </summary>
public static class SaturationMath
{
    /// <summary>Share of the largest possible factor up to which a boost is applied in full.</summary>
    public const float Knee = 0.8f;

    /// <summary>
    /// The factor actually applied: <paramref name="factor"/> up to the knee, then an exponential approach to the
    /// factor at which the smallest channel reaches 0. Never changes a factor ≤ 1 (reducing saturation cannot leave the gamut).
    /// </summary>
    public static float LimitFactor(float factor, float luminance, float minChannel)
    {
        if (factor <= 1f || minChannel >= luminance || luminance <= 0f)
            return factor;
        float max = MathF.Max(luminance / (luminance - minChannel), 1f);
        float knee = MathF.Max(Knee * max, 1f);
        if (factor <= knee)
            return factor;
        if (max - knee < 1e-4f)
            return MathF.Min(factor, max);
        return knee + (max - knee) * (1f - MathF.Exp(-(factor - knee) / (max - knee)));
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
