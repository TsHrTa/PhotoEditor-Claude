namespace PhotoEditor.Core.Adjustments;

/// <summary>Settings converted to the values the per-pixel math uses (shader uniforms / CPU constants).</summary>
public readonly record struct PreparedAdjustments(
    float ExposureGain,
    float ContrastGamma,
    float HighlightsAmount,
    float ShadowsAmount,
    float WhitesAmount,
    float BlacksAmount,
    float HighlightsAmount2,
    float ShadowsAmount2,
    float WhitesAmount2,
    float BlacksAmount2,
    float WhiteBalanceR,
    float WhiteBalanceG,
    float WhiteBalanceB,
    float SaturationFactor,
    float VibranceAmount,
    float[] Hsl,
    bool HasHsl,
    float VignetteStops,
    float VignetteLow,
    float VignetteHigh,
    float VignetteRoundness,
    float SharpenAmount,
    float SharpenRadius,
    float SharpenMasking,
    float SoftenAmount)
{
    /// <summary>
    /// Tap spacing of the soften blur in pixels of an image with this long side: 6 px at 6000 px (the blur
    /// reaches ±3 taps with sigma 1.5 taps, i.e. ±18 px), at least 1. A whole number, so the CPU taps hit pixel centres.
    /// </summary>
    public static int SoftenStepFor(int longSide) => Math.Max(1, (int)Math.Round(longSide / 1000.0));

    /// <summary>Range sigma of the edge-preserving soften blur, on sRGB luminance (0..1).</summary>
    public const float SoftenRangeSigma = 0.1f;

    public bool HasSoften => SoftenAmount > 0f;

    /// <summary>Unsharp-mask strength at slider 100 (detail added = strength × (pixel − blurred)).</summary>
    public const float SharpenStrengthAt100 = 2f;

    public bool HasSharpening => SharpenAmount > 0f;

    /// <summary>Exposure change (stops) at full vignette weight for amount ±100.</summary>
    public const float MaxVignetteStops = 2f;

    public bool HasVignette => VignetteStops != 0f;

    /// <summary>Largest shift (in perceptual units) the highlights slider applies at ±100.</summary>
    public const float MaxHighlightsShift = 0.3f;

    public static PreparedAdjustments From(AdjustmentSettings s)
    {
        var (wbR, wbG, wbB) = WhiteBalanceGains(s.Temperature, s.Tint);
        var (vLow, vHigh) = VignetteBand(s);
        // Each of these steps stays monotonic up to ±100; beyond that (the sliders go to ±200) the same step is
        // applied a second time with the rest, and a monotonic step applied twice is still monotonic.
        var (h1, h2) = Passes(s.Highlights, _ => MaxHighlightsShift);
        // Lifting shadows may be strong; darkening is limited so each step stays monotonic.
        var (sh1, sh2) = Passes(s.Shadows, v => v >= 0 ? 0.25f : 0.14f);
        var (w1, w2) = Passes(s.Whites, _ => 0.15f);
        var (b1, b2) = Passes(s.Blacks, _ => 0.15f);
        return new(
            ExposureGain: MathF.Pow(2f, (float)s.Exposure),
            ContrastGamma: MathF.Exp((float)s.Contrast / 100f * 0.9f),
            HighlightsAmount: h1,
            ShadowsAmount: sh1,
            WhitesAmount: w1,
            BlacksAmount: b1,
            HighlightsAmount2: h2,
            ShadowsAmount2: sh2,
            WhitesAmount2: w2,
            BlacksAmount2: b2,
            WhiteBalanceR: wbR,
            WhiteBalanceG: wbG,
            WhiteBalanceB: wbB,
            SaturationFactor: 1f + (float)s.Saturation / 100f,
            VibranceAmount: (float)s.Vibrance / 100f,
            Hsl: PrepareHsl(s, out bool hasHsl),
            HasHsl: hasHsl,
            VignetteStops: (float)s.VignetteAmount / 100f * MaxVignetteStops,
            VignetteLow: vLow,
            VignetteHigh: vHigh,
            VignetteRoundness: (float)s.VignetteRoundness / 100f,
            SharpenAmount: (float)s.SharpenAmount / 100f * SharpenStrengthAt100,
            SharpenRadius: (float)s.SharpenRadius,
            SharpenMasking: (float)s.SharpenMasking / 100f,
            SoftenAmount: (float)Math.Clamp(s.Soften, 0, 100) / 100f);
    }

    /// <summary>
    /// Splits a slider value (±200) into two passes of at most ±100 each, scaled by the step's strength at 100.
    /// </summary>
    private static (float First, float Second) Passes(double value, Func<double, float> strengthAt100)
    {
        double first = Math.Clamp(value, -100, 100);
        double second = value - first;
        float k = strengthAt100(value);
        return ((float)first / 100f * k, (float)second / 100f * k);
    }

    /// <summary>Transition band of the vignette weight (distance 0 = centre, 1 = corner).</summary>
    private static (float Low, float High) VignetteBand(AdjustmentSettings s)
    {
        float mid = (float)s.VignetteMidpoint / 100f;
        float half = (float)s.VignetteFeather / 100f * 0.5f;
        float low = mid - half, high = mid + half;
        return (low, MathF.Max(high, low + 1e-3f));
    }

    /// <summary>
    /// Per band (3 floats each): hue shift in degrees, saturation factor, luminance in stops.
    /// </summary>
    private static float[] PrepareHsl(AdjustmentSettings s, out bool any)
    {
        var values = new float[HslBands.Count * 3];
        any = false;
        for (int i = 0; i < HslBands.Count; i++)
        {
            var band = HslBands.Get(s, i);
            any |= band != HslBand.Zero;
            values[i * 3] = (float)band.Hue / 100f * HslBands.MaxHueShift;
            values[i * 3 + 1] = 1f + (float)band.Saturation / 100f;
            values[i * 3 + 2] = (float)band.Luminance / 100f * HslBands.MaxLuminanceStops;
        }
        return values;
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
