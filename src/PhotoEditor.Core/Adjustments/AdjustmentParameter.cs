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
    double step = 1)
{
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
        set(settings, Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum));

    public override string ToString() => $"{Group}/{Label}";
}

/// <summary>All adjustment parameters in display order.</summary>
public static class AdjustmentParameters
{
    public const string Light = "Light";
    public const string Color = "Color";

    public static readonly AdjustmentParameter Exposure =
        new(Light, "Exposure", -5, 5, s => s.Exposure, (s, v) => s with { Exposure = v }, format: "+0.00;-0.00;0.00", step: 0.01);

    public static readonly AdjustmentParameter Contrast =
        new(Light, "Contrast", -100, 100, s => s.Contrast, (s, v) => s with { Contrast = v });

    public static readonly AdjustmentParameter Highlights =
        new(Light, "Highlights", -100, 100, s => s.Highlights, (s, v) => s with { Highlights = v });

    public static readonly AdjustmentParameter Shadows =
        new(Light, "Shadows", -100, 100, s => s.Shadows, (s, v) => s with { Shadows = v });

    public static readonly AdjustmentParameter Whites =
        new(Light, "Whites", -100, 100, s => s.Whites, (s, v) => s with { Whites = v });

    public static readonly AdjustmentParameter Blacks =
        new(Light, "Blacks", -100, 100, s => s.Blacks, (s, v) => s with { Blacks = v });

    public static readonly AdjustmentParameter Temperature =
        new(Color, "Temperature", -100, 100, s => s.Temperature, (s, v) => s with { Temperature = v });

    public static readonly AdjustmentParameter Tint =
        new(Color, "Tint", -100, 100, s => s.Tint, (s, v) => s with { Tint = v });

    public static readonly AdjustmentParameter Vibrance =
        new(Color, "Vibrance", -100, 100, s => s.Vibrance, (s, v) => s with { Vibrance = v });

    public static readonly AdjustmentParameter Saturation =
        new(Color, "Saturation", -100, 100, s => s.Saturation, (s, v) => s with { Saturation = v });

    public static readonly IReadOnlyList<AdjustmentParameter> All =
    [
        Exposure, Contrast, Highlights, Shadows, Whites, Blacks,
        Temperature, Tint, Vibrance, Saturation,
    ];
}
