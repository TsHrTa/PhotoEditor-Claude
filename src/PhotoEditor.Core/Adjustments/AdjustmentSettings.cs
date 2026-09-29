namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Immutable set of global adjustments. The original image is never changed;
/// these values are applied on every render (preview shader and CPU export).
/// All values are 0 when neutral.
/// </summary>
public sealed record AdjustmentSettings
{
    public static readonly AdjustmentSettings Default = new();

    /// <summary>Exposure in stops (EV), -5..+5.</summary>
    public double Exposure { get; init; }

    /// <summary>-100..+100 for this and the following tone values.</summary>
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }

    /// <summary>-100 (cooler/blue) .. +100 (warmer/yellow).</summary>
    public double Temperature { get; init; }

    /// <summary>-100 (green) .. +100 (magenta).</summary>
    public double Tint { get; init; }

    /// <summary>-100 (greyscale) .. +100.</summary>
    public double Saturation { get; init; }

    /// <summary>-100..+100; like saturation but affects muted colours more than saturated ones.</summary>
    public double Vibrance { get; init; }

    public bool IsDefault => this == Default;
}
