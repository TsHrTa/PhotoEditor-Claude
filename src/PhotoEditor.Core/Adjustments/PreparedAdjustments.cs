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
    float SoftenAmount,
    float DehazeAmount,
    float NoiseLuminanceAmount,
    float NoiseColorAmount,
    float DefringePurpleAmount,
    float DefringeGreenAmount,
    float PurpleHueFrom,
    float PurpleHueTo,
    float GreenHueFrom,
    float GreenHueTo,
    float TextureAmount,
    float ClarityAmount,
    float LensVignettingAmount)
{
    /// <summary>Hue (degrees) of Lightroom's purple / green defringe range ends 0 and 100.</summary>
    public const float PurpleHue0 = 240f, PurpleHue100 = 350f, GreenHue0 = 40f, GreenHue100 = 190f;

    /// <summary>Fade (degrees) outside a defringe hue range.</summary>
    public const float DefringeHueFade = 20f;

    /// <summary>A hue range in Lightroom's units as degrees, ends in order.</summary>
    private static (float From, float To) HueRange(double low, double high, float hue0, float hue100)
    {
        double lo = Math.Clamp(Math.Min(low, high), 0, 100), hi = Math.Clamp(Math.Max(low, high), 0, 100);
        return ((float)(hue0 + (hue100 - hue0) * lo / 100), (float)(hue0 + (hue100 - hue0) * hi / 100));
    }

    /// <summary>
    /// Weight 0..1 of hue <paramref name="h"/> (degrees) in the range [from, to] with soft edges; handles the
    /// wrap at 360°. Mirrored in the shader.
    /// </summary>
    public static float HueRangeWeight(float h, float from, float to)
    {
        float Band(float x) => ToneCurve.SmoothStep(from - DefringeHueFade, from, x) * (1f - ToneCurve.SmoothStep(to, to + DefringeHueFade, x));
        return MathF.Max(Band(h), MathF.Max(Band(h + 360f), Band(h - 360f)));
    }

    /// <summary>Highlights or shadows are set (applied locally on the base brightness).</summary>
    public bool HasLocalTone => HighlightsAmount != 0f || ShadowsAmount != 0f;

    public bool HasClarity => ClarityAmount != 0f;

    /// <summary>The pass needs the photo's base brightness (<see cref="ToneBaseMap"/>).</summary>
    public bool NeedsToneBase => HasLocalTone || HasClarity;

    public bool HasTexture => TextureAmount != 0f;

    /// <summary>Texture: medium-size detail added (× this) at slider 100.</summary>
    public const float TextureStrengthAt100 = 1f;

    /// <summary>Clarity: extra local contrast (in stops, × this) at slider 100, at full midtone weight.</summary>
    public const float ClarityStrengthAt100 = 0.6f;

    /// <summary>Largest brightening / darkening (factor) clarity may apply to a pixel.</summary>
    public const float MaxClarityGain = 4f;

    public bool HasNoiseReduction => NoiseLuminanceAmount > 0f || NoiseColorAmount > 0f;
    public bool HasDefringe => DefringePurpleAmount > 0f || DefringeGreenAmount > 0f;

    /// <summary>Tap spacing (full-resolution pixels) of the luminance noise filter, the colour noise blur and the fringe edge test.</summary>
    public const int LumaNoiseStep = 1, ColorNoiseStep = 2, FringeStep = 2;

    public bool HasDehaze => DehazeAmount != 0f;

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
            SoftenAmount: (float)Math.Clamp(s.Soften, 0, 100) / 100f,
            DehazeAmount: (float)Math.Clamp(s.Dehaze, -100, 100) / 100f,
            NoiseLuminanceAmount: (float)Math.Clamp(s.NoiseLuminance, 0, 100) / 100f,
            NoiseColorAmount: (float)Math.Clamp(s.NoiseColor, 0, 100) / 100f,
            DefringePurpleAmount: (float)Math.Clamp(s.DefringePurple, 0, 100) / 100f,
            DefringeGreenAmount: (float)Math.Clamp(s.DefringeGreen, 0, 100) / 100f,
            PurpleHueFrom: HueRange(s.DefringePurpleHueLow, s.DefringePurpleHueHigh, PurpleHue0, PurpleHue100).From,
            PurpleHueTo: HueRange(s.DefringePurpleHueLow, s.DefringePurpleHueHigh, PurpleHue0, PurpleHue100).To,
            GreenHueFrom: HueRange(s.DefringeGreenHueLow, s.DefringeGreenHueHigh, GreenHue0, GreenHue100).From,
            GreenHueTo: HueRange(s.DefringeGreenHueLow, s.DefringeGreenHueHigh, GreenHue0, GreenHue100).To,
            TextureAmount: (float)Math.Clamp(s.Texture, -100, 200) / 100f * TextureStrengthAt100,
            ClarityAmount: (float)Math.Clamp(s.Clarity, -100, 200) / 100f * ClarityStrengthAt100,
            LensVignettingAmount: (float)Math.Clamp(s.LensVignetting, -100, 100) / 100f);
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
