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
    string format = "0")
{
    public string Group { get; } = group;
    public string Label { get; } = label;
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
    public double DefaultValue { get; } = defaultValue;

    /// <summary>.NET number format used to display the value.</summary>
    public string Format { get; } = format;

    public double Get(AdjustmentSettings settings) => get(settings);

    public AdjustmentSettings Set(AdjustmentSettings settings, double value) =>
        set(settings, Math.Clamp(value, Minimum, Maximum));

    public override string ToString() => $"{Group}/{Label}";
}

/// <summary>All adjustment parameters in display order.</summary>
public static class AdjustmentParameters
{
    public const string Light = "Light";

    public static readonly AdjustmentParameter Exposure =
        new(Light, "Exposure", -5, 5, s => s.Exposure, (s, v) => s with { Exposure = v }, format: "+0.00;-0.00;0.00");

    public static readonly IReadOnlyList<AdjustmentParameter> All = [Exposure];
}
