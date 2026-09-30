using System.Collections.Immutable;
using System.Text.Json.Serialization;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Core.Presets;

/// <summary>
/// An adaptive preset: a recipe of steps run in order on a photo. Steps can set fixed slider values, run
/// Auto, create masks (by detection or from stored geometry) and state goals ("subject at least 15 % brighter
/// than the background") that are measured on the photo and only corrected when not met.
/// </summary>
public sealed record Preset
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Name { get; init; } = "Preset";
    public ImmutableList<PresetStep> Steps { get; init; } = [];

    /// <summary>Built-in presets are shown with the user's but cannot be deleted or overwritten.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    public bool Equals(Preset? other) =>
        other is not null && Version == other.Version && Name == other.Name && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode() => HashCode.Combine(Name, Steps.Count);
}

/// <summary>One step of a <see cref="Preset"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AutoStep), "auto")]
[JsonDerivedType(typeof(SetValuesStep), "set")]
[JsonDerivedType(typeof(AddMaskStep), "mask")]
[JsonDerivedType(typeof(GoalStep), "goal")]
[JsonDerivedType(typeof(CurvesStep), "curves")]
public abstract record PresetStep;

/// <summary>Sets the whole image's point curves (tone curve panel).</summary>
public sealed record CurvesStep : PresetStep
{
    public Adjustments.PointCurve Curve { get; init; } = Adjustments.PointCurve.Linear;
    public Adjustments.PointCurve CurveRed { get; init; } = Adjustments.PointCurve.Linear;
    public Adjustments.PointCurve CurveGreen { get; init; } = Adjustments.PointCurve.Linear;
    public Adjustments.PointCurve CurveBlue { get; init; } = Adjustments.PointCurve.Linear;
}

/// <summary>Runs Auto on the whole image (light, white balance, vibrance).</summary>
public sealed record AutoStep : PresetStep;

/// <summary>Sets (or, when <see cref="Relative"/>, adds to) slider values of the whole image or a mask.</summary>
public sealed record SetValuesStep : PresetStep
{
    /// <summary>Mask name, or null for the whole image.</summary>
    public string? Mask { get; init; }

    /// <summary>Slider id (see <see cref="Adjustments.AdjustmentParameter.Id"/>) → value.</summary>
    public ImmutableSortedDictionary<string, double> Values { get; init; } = ImmutableSortedDictionary<string, double>.Empty;

    /// <summary>Add the values to the current ones instead of replacing them.</summary>
    public bool Relative { get; init; }

    public bool Equals(SetValuesStep? other) =>
        other is not null && Mask == other.Mask && Relative == other.Relative && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Mask, Relative, Values.Count);
}

/// <summary>Where a preset mask comes from.</summary>
public enum MaskSource
{
    /// <summary>The main subject (AI).</summary>
    Subject,

    /// <summary>All people (AI).</summary>
    People,

    /// <summary>The sky (AI).</summary>
    Sky,

    /// <summary>Everything except the subject (AI).</summary>
    Background,

    /// <summary>Linear gradient from the top edge to the middle (a simple "sky" without AI).</summary>
    TopGradient,

    /// <summary>Linear gradient from the bottom edge to the middle.</summary>
    BottomGradient,

    /// <summary>Radial gradient in the centre.</summary>
    CenterRadial,

    /// <summary>The components stored in <see cref="AddMaskStep.Components"/> (e.g. saved from a photo).</summary>
    Geometry,
}

/// <summary>Creates (or replaces) the mask <see cref="Name"/>; its sliders are set by later steps.</summary>
public sealed record AddMaskStep : PresetStep
{
    public string Name { get; init; } = "Mask";
    public MaskSource Source { get; init; } = MaskSource.Subject;

    /// <summary>Only for <see cref="MaskSource.Geometry"/>.</summary>
    public ImmutableList<MaskComponent> Components { get; init; } = [];

    public bool Equals(AddMaskStep? other) =>
        other is not null && Name == other.Name && Source == other.Source && Components.SequenceEqual(other.Components);

    public override int GetHashCode() => HashCode.Combine(Name, Source, Components.Count);
}

/// <summary>What a goal measures in a region (all on a 0..100-like scale).</summary>
public enum GoalMetric
{
    /// <summary>Median brightness as perceived (0 = black, 100 = white).</summary>
    Brightness,

    /// <summary>Mean colour saturation (0 = grey, 100 = fully saturated).</summary>
    Saturation,

    /// <summary>Mean warmth, red vs. blue (-100 = blue, +100 = red/yellow; 0 = neutral).</summary>
    Warmth,

    /// <summary>Spread of brightness (standard deviation, perceived).</summary>
    Contrast,
}

public enum GoalRelation
{
    AtLeast,
    AtMost,
    About,
}

/// <summary>A part of the photo: the whole image, a mask, or everything outside a mask.</summary>
public sealed record Region(string? Mask = null, bool Outside = false)
{
    public static readonly Region WholeImage = new();

    public override string ToString() => Mask is null ? "whole image" : Outside ? $"outside {Mask}" : Mask;
}

/// <summary>
/// "<see cref="Region"/>'s <see cref="Metric"/> should be <see cref="Relation"/> <see cref="Target"/>" — relative to
/// <see cref="Reference"/> when given (Brightness / Saturation / Contrast in percent, Warmth in points), else an
/// absolute value. When not met, the slider <see cref="FixBy"/> of <see cref="FixMask"/> is changed by at most
/// <see cref="MaxChange"/> to meet it.
/// </summary>
public sealed record GoalStep : PresetStep
{
    public Region Region { get; init; } = Region.WholeImage;
    public GoalMetric Metric { get; init; } = GoalMetric.Brightness;
    public GoalRelation Relation { get; init; } = GoalRelation.AtLeast;
    public Region? Reference { get; init; }
    public double Target { get; init; }

    /// <summary>Slider id used to correct, e.g. "exposure".</summary>
    public string FixBy { get; init; } = "exposure";

    /// <summary>Mask whose slider is changed; null = the region's own mask (the whole image for "whole image" / "outside").</summary>
    public string? FixMask { get; init; }

    /// <summary>Change the whole image's slider instead of a mask's (overrides <see cref="FixMask"/>).</summary>
    public bool FixWholeImage { get; init; }

    /// <summary>Largest change of the slider (in slider units, e.g. 1.5 EV).</summary>
    public double MaxChange { get; init; } = 1.5;

    /// <summary>A goal missed by at most this much (metric units) counts as met.</summary>
    public double Tolerance { get; init; } = 1;
}
