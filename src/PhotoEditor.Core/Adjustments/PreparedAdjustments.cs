namespace PhotoEditor.Core.Adjustments;

/// <summary>Settings converted to the values the per-pixel math uses (shader uniforms / CPU constants).</summary>
public readonly record struct PreparedAdjustments(float ExposureGain)
{
    public static PreparedAdjustments From(AdjustmentSettings s) => new(
        ExposureGain: MathF.Pow(2f, (float)s.Exposure));
}
