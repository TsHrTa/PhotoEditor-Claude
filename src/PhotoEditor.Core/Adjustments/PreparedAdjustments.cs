namespace PhotoEditor.Core.Adjustments;

/// <summary>Settings converted to the values the per-pixel math uses (shader uniforms / CPU constants).</summary>
public readonly record struct PreparedAdjustments(
    float ExposureGain,
    float ContrastGamma,
    float HighlightsAmount,
    float ShadowsAmount,
    float WhitesAmount,
    float BlacksAmount)
{
    /// <summary>Largest shift (in perceptual units) the highlights slider applies at ±100.</summary>
    public const float MaxHighlightsShift = 0.3f;

    public static PreparedAdjustments From(AdjustmentSettings s) => new(
        ExposureGain: MathF.Pow(2f, (float)s.Exposure),
        ContrastGamma: MathF.Exp((float)s.Contrast / 100f * 0.9f),
        HighlightsAmount: (float)s.Highlights / 100f * MaxHighlightsShift,
        // Lifting shadows may be strong; darkening is limited so the curve stays monotonic.
        ShadowsAmount: (float)s.Shadows / 100f * (s.Shadows >= 0 ? 0.25f : 0.14f),
        WhitesAmount: (float)s.Whites / 100f * 0.15f,
        BlacksAmount: (float)s.Blacks / 100f * 0.15f);
}
