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

    public bool IsDefault => this == Default;
}
