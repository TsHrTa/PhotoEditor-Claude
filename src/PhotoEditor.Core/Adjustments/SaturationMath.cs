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

    /// <summary>
    /// Saturation: factor on the amount for a grey-ish colour (the RGB scaling moves OKLab chroma less than its factor),
    /// and the OKLab chroma at which +100 stops boosting (it fades linearly from full at grey).
    /// </summary>
    public const float SaturationStrength = 1.75f, SaturationReach = 0.145f;

    /// <summary>Vibrance: share of its amount applied to a grey-ish colour, and the OKLab chroma where it reaches 0.</summary>
    public const float VibranceWeight = 0.6f, VibranceReach = 0.25f;

    /// <summary>
    /// The factor on a colour's distance from its luminance for <paramref name="saturationFactor"/> (1 + Saturation / 100)
    /// and <paramref name="vibrance"/> (Vibrance / 100) at OKLab <paramref name="chroma"/>. As Lightroom (measured on its
    /// TIFFs of the R8 sample, <c>Bench/SliderFitBench</c>): a raised Saturation boosts muted colours fully and vivid ones
    /// less, linearly down to none at <see cref="SaturationReach"/> (+65 gave × 1.58 to muted, × 1.01 to the most vivid
    /// colours); above +100 that reach grows with the amount, so the slider goes further than Lightroom's and finally
    /// moves vivid colours too. Lowered, it scales all colours alike (−100 = grey). Vibrance works the same way, weaker and
    /// on muted colours only. Mirrored in the shader.
    /// </summary>
    public static float Factor(float chroma, float saturationFactor, float vibrance)
    {
        float s = saturationFactor - 1f;
        float sat = s > 0f
            ? 1f + s * SaturationStrength * MathF.Max(1f - chroma / (SaturationReach * MathF.Max(s, 1f)), 0f)
            : saturationFactor;
        float vib = 1f + vibrance * VibranceWeight * MathF.Max(1f - chroma / (VibranceReach * MathF.Max(vibrance, 1f)), 0f);
        return MathF.Max(sat * vib, 0f);
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
