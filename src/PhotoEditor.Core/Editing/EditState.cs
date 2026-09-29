using System.Collections.Immutable;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Core.Editing;

/// <summary>The complete, immutable edit of one image: global adjustments plus local masks.</summary>
public sealed record EditState
{
    public static readonly EditState Default = new();

    public AdjustmentSettings Adjustments { get; init; } = AdjustmentSettings.Default;

    /// <summary>Applied in order on top of the global adjustments.</summary>
    public ImmutableList<Mask> Masks { get; init; } = [];

    /// <summary>Applied last (after all adjustments); the vignette is relative to the cropped frame.</summary>
    public Crop Crop { get; init; } = Crop.None;

    public bool IsDefault => Equals(Default);

    public bool Equals(EditState? other) =>
        other is not null && Adjustments == other.Adjustments && Crop == other.Crop && Masks.SequenceEqual(other.Masks);

    public override int GetHashCode() => HashCode.Combine(Adjustments, Crop, Masks.Count);
}
