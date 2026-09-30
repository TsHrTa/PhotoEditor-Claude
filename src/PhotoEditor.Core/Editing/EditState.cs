using System.Collections.Immutable;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;

namespace PhotoEditor.Core.Editing;

/// <summary>The complete, immutable edit of one image: spot removal, global adjustments plus local masks.</summary>
public sealed record EditState
{
    public static readonly EditState Default = new();

    public AdjustmentSettings Adjustments { get; init; } = AdjustmentSettings.Default;

    /// <summary>Spot removal, applied in order to the photo's pixels before all adjustments.</summary>
    public ImmutableList<Spot> Spots { get; init; } = [];

    /// <summary>Applied in order on top of the global adjustments.</summary>
    public ImmutableList<Mask> Masks { get; init; } = [];

    /// <summary>Applied last (after all adjustments); the vignette is relative to the cropped frame.</summary>
    public Crop Crop { get; init; } = Crop.None;

    /// <summary>Rotation / flip of the finished picture (after the crop).</summary>
    public PhotoOrientation Orientation { get; init; }

    public bool IsDefault => Equals(Default);

    public bool Equals(EditState? other) =>
        other is not null && Adjustments == other.Adjustments && Crop == other.Crop && Orientation.Turns == other.Orientation.Turns
        && Orientation.Flip == other.Orientation.Flip && Masks.SequenceEqual(other.Masks) && Spots.SequenceEqual(other.Spots);

    public override int GetHashCode() => HashCode.Combine(Adjustments, Crop, Masks.Count, Spots.Count);
}
