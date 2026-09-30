namespace PhotoEditor.Core.Adjustments;

/// <summary>Describes one slider-controlled value of <see cref="AdjustmentSettings"/>.</summary>
public sealed class AdjustmentParameter(
    string group,
    string label,
    double minimum,
    double maximum,
    Func<AdjustmentSettings, double> get,
    Func<AdjustmentSettings, double, AdjustmentSettings> set,
    double defaultValue = 0,
    string format = "0",
    double step = 1,
    string? id = null)
{
    /// <summary>Stable identifier used in presets, e.g. "exposure" or "hue.blues".</summary>
    public string Id { get; } = id ?? $"{group}.{label}";

    public string Group { get; } = group;
    public string Label { get; } = label;
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
    public double DefaultValue { get; } = defaultValue;

    /// <summary>Values are rounded to a multiple of this.</summary>
    public double Step { get; } = step;

    /// <summary>.NET number format used to display the value.</summary>
    public string Format { get; } = format;

    public double Get(AdjustmentSettings settings) => get(settings);

    public AdjustmentSettings Set(AdjustmentSettings settings, double value) =>
        // Round again to drop float noise (-70 × 0.01 = -0.7000000000000001); "+ 0.0" turns -0 into 0.
        set(settings, Math.Clamp(Math.Round(Math.Round(value / Step) * Step, 10), Minimum, Maximum) + 0.0);

    public override string ToString() => $"{Group}/{Label}";
}

/// <summary>All adjustment parameters in display order.</summary>
public static class AdjustmentParameters
{
    public const string Light = "Light";
    public const string Color = "Color";
    public const string Vignette = "Vignette";
    public const string Detail = "Detail";
    public const string Effects = "Effects";
    public const string Lens = "Lens corrections";
    public const string HslHue = "HSL · Hue";
    public const string HslSaturation = "HSL · Saturation";
    public const string HslLuminance = "HSL · Luminance";

    public static readonly AdjustmentParameter Exposure =
        new(Light, "Exposure", -10, 10, s => s.Exposure, (s, v) => s with { Exposure = v }, format: "+0.00;-0.00;0.00", step: 0.01, id: "exposure");

    public static readonly AdjustmentParameter Contrast =
        new(Light, "Contrast", -200, 200, s => s.Contrast, (s, v) => s with { Contrast = v }, id: "contrast");

    public static readonly AdjustmentParameter Highlights =
        new(Light, "Highlights", -200, 200, s => s.Highlights, (s, v) => s with { Highlights = v }, id: "highlights");

    public static readonly AdjustmentParameter Shadows =
        new(Light, "Shadows", -200, 200, s => s.Shadows, (s, v) => s with { Shadows = v }, id: "shadows");

    public static readonly AdjustmentParameter Whites =
        new(Light, "Whites", -200, 200, s => s.Whites, (s, v) => s with { Whites = v }, id: "whites");

    public static readonly AdjustmentParameter Blacks =
        new(Light, "Blacks", -200, 200, s => s.Blacks, (s, v) => s with { Blacks = v }, id: "blacks");

    public static readonly AdjustmentParameter Temperature =
        new(Color, "Temperature", -200, 200, s => s.Temperature, (s, v) => s with { Temperature = v }, id: "temperature");

    public static readonly AdjustmentParameter Tint =
        new(Color, "Tint", -200, 200, s => s.Tint, (s, v) => s with { Tint = v }, id: "tint");

    public static readonly AdjustmentParameter Vibrance =
        new(Color, "Vibrance", -100, 200, s => s.Vibrance, (s, v) => s with { Vibrance = v }, id: "vibrance");

    public static readonly AdjustmentParameter Saturation =
        new(Color, "Saturation", -100, 200, s => s.Saturation, (s, v) => s with { Saturation = v }, id: "saturation");

    public static readonly AdjustmentParameter VignetteAmount =
        new(Vignette, "Amount", -200, 200, s => s.VignetteAmount, (s, v) => s with { VignetteAmount = v }, id: "vignetteAmount");

    public static readonly AdjustmentParameter VignetteMidpoint =
        new(Vignette, "Midpoint", 0, 100, s => s.VignetteMidpoint, (s, v) => s with { VignetteMidpoint = v }, defaultValue: 50, id: "vignetteMidpoint");

    public static readonly AdjustmentParameter VignetteRoundness =
        new(Vignette, "Roundness", -100, 100, s => s.VignetteRoundness, (s, v) => s with { VignetteRoundness = v }, id: "vignetteRoundness");

    public static readonly AdjustmentParameter VignetteFeather =
        new(Vignette, "Feather", 0, 100, s => s.VignetteFeather, (s, v) => s with { VignetteFeather = v }, defaultValue: 50, id: "vignetteFeather");

    public static readonly AdjustmentParameter SharpenAmount =
        new(Detail, "Sharpening", 0, 300, s => s.SharpenAmount, (s, v) => s with { SharpenAmount = v }, id: "sharpenAmount");

    public static readonly AdjustmentParameter SharpenRadius =
        new(Detail, "Radius", 0.5, 3, s => s.SharpenRadius, (s, v) => s with { SharpenRadius = v }, defaultValue: 1, format: "0.0", step: 0.1, id: "sharpenRadius");

    public static readonly AdjustmentParameter SharpenMasking =
        new(Detail, "Masking", 0, 100, s => s.SharpenMasking, (s, v) => s with { SharpenMasking = v }, id: "sharpenMasking");

    public static readonly AdjustmentParameter NoiseLuminance =
        new(Detail, "Noise: luminance", 0, 100, s => s.NoiseLuminance, (s, v) => s with { NoiseLuminance = v }, id: "noiseLuminance");

    public static readonly AdjustmentParameter NoiseColor =
        new(Detail, "Noise: color", 0, 100, s => s.NoiseColor, (s, v) => s with { NoiseColor = v }, id: "noiseColor");

    public static readonly AdjustmentParameter DefringePurple =
        new(Lens, "Defringe purple", 0, 100, s => s.DefringePurple, (s, v) => s with { DefringePurple = v }, id: "defringePurple");

    public static readonly AdjustmentParameter DefringeGreen =
        new(Lens, "Defringe green", 0, 100, s => s.DefringeGreen, (s, v) => s with { DefringeGreen = v }, id: "defringeGreen");

    public static readonly AdjustmentParameter LensDistortion =
        new(Lens, "Distortion", -100, 100, s => s.LensDistortion, (s, v) => s with { LensDistortion = v }, id: "lensDistortion");

    public static readonly AdjustmentParameter LensVignetting =
        new(Lens, "Vignetting", -100, 100, s => s.LensVignetting, (s, v) => s with { LensVignetting = v }, id: "lensVignetting");

    public static readonly AdjustmentParameter DefringePurpleHueLow =
        new(Lens, "Purple hue from", 0, 100, s => s.DefringePurpleHueLow, (s, v) => s with { DefringePurpleHueLow = v }, defaultValue: 30, id: "defringePurpleHueLow");

    public static readonly AdjustmentParameter DefringePurpleHueHigh =
        new(Lens, "Purple hue to", 0, 100, s => s.DefringePurpleHueHigh, (s, v) => s with { DefringePurpleHueHigh = v }, defaultValue: 70, id: "defringePurpleHueHigh");

    public static readonly AdjustmentParameter DefringeGreenHueLow =
        new(Lens, "Green hue from", 0, 100, s => s.DefringeGreenHueLow, (s, v) => s with { DefringeGreenHueLow = v }, defaultValue: 40, id: "defringeGreenHueLow");

    public static readonly AdjustmentParameter DefringeGreenHueHigh =
        new(Lens, "Green hue to", 0, 100, s => s.DefringeGreenHueHigh, (s, v) => s with { DefringeGreenHueHigh = v }, defaultValue: 60, id: "defringeGreenHueHigh");

    public static readonly AdjustmentParameter DenoiseAmount =
        new(Detail, "Denoise (AI)", 0, 100, s => s.DenoiseAmount, (s, v) => s with { DenoiseAmount = v }, id: "denoiseAmount");

    public static readonly AdjustmentParameter DeblurAmount =
        new(Detail, "Deblur (AI)", 0, 100, s => s.DeblurAmount, (s, v) => s with { DeblurAmount = v }, id: "deblurAmount");

    public static readonly AdjustmentParameter Texture =
        new(Effects, "Texture", -100, 200, s => s.Texture, (s, v) => s with { Texture = v }, id: "texture");

    public static readonly AdjustmentParameter Clarity =
        new(Effects, "Clarity", -100, 200, s => s.Clarity, (s, v) => s with { Clarity = v }, id: "clarity");

    public static readonly AdjustmentParameter Dehaze =
        new(Effects, "Dehaze", -100, 100, s => s.Dehaze, (s, v) => s with { Dehaze = v }, id: "dehaze");

    public static readonly AdjustmentParameter Soften =
        new(Effects, "Soften", 0, 100, s => s.Soften, (s, v) => s with { Soften = v }, id: "soften");

    /// <summary>Parameters that only apply to the whole image (not inside masks).</summary>
    public static readonly IReadOnlyList<AdjustmentParameter> GlobalOnly =
        [DenoiseAmount, DeblurAmount, SharpenAmount, SharpenRadius, SharpenMasking, NoiseLuminance, NoiseColor,
         LensDistortion, LensVignetting, DefringePurple, DefringePurpleHueLow, DefringePurpleHueHigh, DefringeGreen, DefringeGreenHueLow, DefringeGreenHueHigh];

    public static readonly IReadOnlyList<AdjustmentParameter> Hsl = CreateHsl();

    public static readonly IReadOnlyList<AdjustmentParameter> All =
    [
        Exposure, Contrast, Highlights, Shadows, Whites, Blacks,
        Temperature, Tint, Vibrance, Saturation,
        .. Hsl,
        VignetteAmount, VignetteMidpoint, VignetteRoundness, VignetteFeather,
        Texture, Clarity, Dehaze, Soften,
        DenoiseAmount, DeblurAmount, SharpenAmount, SharpenRadius, SharpenMasking, NoiseLuminance, NoiseColor,
        LensDistortion, LensVignetting, DefringePurple, DefringePurpleHueLow, DefringePurpleHueHigh, DefringeGreen, DefringeGreenHueLow, DefringeGreenHueHigh,
    ];

    /// <summary>Parameter with the given <see cref="AdjustmentParameter.Id"/>, or null.</summary>
    public static AdjustmentParameter? ById(string id) => All.FirstOrDefault(p => p.Id == id);

    private static List<AdjustmentParameter> CreateHsl()
    {
        var list = new List<AdjustmentParameter>();
        foreach (var (group, get, with) in new (string, Func<HslBand, double>, Func<HslBand, double, HslBand>)[]
        {
            (HslHue, b => b.Hue, (b, v) => b with { Hue = v }),
            (HslSaturation, b => b.Saturation, (b, v) => b with { Saturation = v }),
            (HslLuminance, b => b.Luminance, (b, v) => b with { Luminance = v }),
        })
        {
            for (int i = 0; i < HslBands.Count; i++)
            {
                int band = i;
                string kind = group == HslHue ? "hue" : group == HslSaturation ? "saturation" : "luminance";
                // Saturation −100 is already grey; the other two go to ±200.
                list.Add(new AdjustmentParameter(group, HslBands.Names[band], group == HslSaturation ? -100 : -200, 200,
                    s => get(HslBands.Get(s, band)),
                    (s, v) => HslBands.With(s, band, with(HslBands.Get(s, band), v)),
                    id: $"{kind}.{HslBands.Names[band].ToLowerInvariant()}"));
            }
        }
        return list;
    }
}
