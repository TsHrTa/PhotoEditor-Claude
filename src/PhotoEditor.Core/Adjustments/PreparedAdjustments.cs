namespace PhotoEditor.Core.Adjustments;

/// <summary>Settings converted to the values the per-pixel math uses (shader uniforms / CPU constants).</summary>
public readonly record struct PreparedAdjustments(
    float ExposureGain,
    float ContrastGamma,
    float HighlightsAmount,
    float ShadowsAmount,
    float WhitesAmount,
    float BlacksAmount,
    float WhiteBalanceR,
    float WhiteBalanceG,
    float WhiteBalanceB,
    float SaturationFactor,
    float VibranceAmount)
{
    /// <summary>Largest shift (in perceptual units) the highlights slider applies at ±100.</summary>
    public const float MaxHighlightsShift = 0.3f;

    public static PreparedAdjustments From(AdjustmentSettings s)
    {
        var (wbR, wbG, wbB) = WhiteBalanceGains(s.Temperature, s.Tint);
        return new(
            ExposureGain: MathF.Pow(2f, (float)s.Exposure),
            ContrastGamma: MathF.Exp((float)s.Contrast / 100f * 0.9f),
            HighlightsAmount: (float)s.Highlights / 100f * MaxHighlightsShift,
            // Lifting shadows may be strong; darkening is limited so the curve stays monotonic.
            ShadowsAmount: (float)s.Shadows / 100f * (s.Shadows >= 0 ? 0.25f : 0.14f),
            WhitesAmount: (float)s.Whites / 100f * 0.15f,
            BlacksAmount: (float)s.Blacks / 100f * 0.15f,
            WhiteBalanceR: wbR,
            WhiteBalanceG: wbG,
            WhiteBalanceB: wbB,
            SaturationFactor: 1f + (float)s.Saturation / 100f,
            VibranceAmount: (float)s.Vibrance / 100f);
    }

    /// <summary>
    /// Linear RGB multipliers for temperature (red/blue) and tint (green), normalised so
    /// the luminance of a grey stays the same.
    /// </summary>
    public static (float R, float G, float B) WhiteBalanceGains(double temperature, double tint)
    {
        float t = (float)temperature / 100f, n = (float)tint / 100f;
        float r = MathF.Exp(t * 0.35f);
        float b = MathF.Exp(-t * 0.35f);
        float g = MathF.Exp(-n * 0.25f);
        float y = ToneCurve.Luminance(r, g, b);
        return (r / y, g / y, b / y);
    }
}
