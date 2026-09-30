using System.Collections.Immutable;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;

namespace PhotoEditor.Core.Editing;

/// <summary>The complete, immutable edit of one image: spot removal, global adjustments plus local masks.</summary>
public sealed record EditState
{
    public static readonly EditState Default = new();

    /// <summary>An unedited RAW photo: <see cref="AdjustmentSettings.RawDefault"/>.</summary>
    public static readonly EditState RawDefault = new() { Adjustments = AdjustmentSettings.RawDefault };

    /// <summary>The edit a photo starts with (and "Reset all" goes back to).</summary>
    public static EditState DefaultFor(bool isRaw) => isRaw ? RawDefault : Default;

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

    /// <summary>True when this is the unedited state of a RAW (<paramref name="isRaw"/>) or other photo.</summary>
    public bool IsDefaultFor(bool isRaw) => Equals(DefaultFor(isRaw));

    public bool Equals(EditState? other) =>
        other is not null && Adjustments == other.Adjustments && Crop == other.Crop && Orientation.Turns == other.Orientation.Turns
        && Orientation.Flip == other.Orientation.Flip && Masks.SequenceEqual(other.Masks) && Spots.SequenceEqual(other.Spots);

    public override int GetHashCode() => HashCode.Combine(Adjustments, Crop, Masks.Count, Spots.Count);
}
