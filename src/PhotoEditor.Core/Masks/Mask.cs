using System.Collections.Immutable;
using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.Core.Masks;

/// <summary>A local adjustment: a grayscale mask built from components, with its own adjustment set.</summary>
public sealed record Mask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Mask";
    public bool Enabled { get; init; } = true;

    /// <summary>Applied on top of the global adjustments where the mask is white.</summary>
    public AdjustmentSettings Adjustments { get; init; } = AdjustmentSettings.Default;

    public ImmutableList<MaskComponent> Components { get; init; } = [];

    /// <summary>True when the mask can change the image.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Enabled && !Adjustments.IsDefault && Components.Count > 0;

    public bool Equals(Mask? other) =>
        other is not null && Id == other.Id && Name == other.Name && Enabled == other.Enabled
        && Adjustments == other.Adjustments && Components.SequenceEqual(other.Components);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Enabled, Adjustments, Components.Count);
}
