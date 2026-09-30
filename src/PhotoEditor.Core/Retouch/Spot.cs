using System.Collections.Immutable;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Core.Retouch;

public enum SpotMode
{
    /// <summary>Texture from the source, colour and brightness blended to fit the destination's surroundings.</summary>
    Heal,

    /// <summary>A straight copy of the source.</summary>
    Clone,

    /// <summary>
    /// AI remove: the painted <see cref="Spot.Path"/> is filled by an inpainting model (LaMa); the result is kept in
    /// the fill store (<see cref="Spot.Fill"/>) so every render uses the same fill.
    /// </summary>
    Remove,
}

/// <summary>
/// One spot removal: the circle at <see cref="Center"/> is replaced by the circle at <see cref="Source"/>; for
/// <see cref="SpotMode.Remove"/>, the brush <see cref="Path"/> (radius <see cref="Radius"/>) is filled by AI.
/// Positions are normalised full-image coordinates (0..1, like masks); the radius is a fraction of the image's
/// longer side, so a spot covers the same part of the photo at preview and export resolution.
/// </summary>
public sealed record Spot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SpotMode Mode { get; init; } = SpotMode.Heal;
    public BrushPoint Center { get; init; } = new(0.5f, 0.5f);
    public BrushPoint Source { get; init; } = new(0.6f, 0.5f);

    /// <summary>Radius as a fraction of the longer image side.</summary>
    public float Radius { get; init; } = 0.02f;

    /// <summary>0 = hard edge, 1 = fades from the centre.</summary>
    public float Feather { get; init; } = 0.5f;

    /// <summary>0..1.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Remove: the painted stroke's points (normalised; one point = a dot).</summary>
    public ImmutableList<BrushPoint> Path { get; init; } = [];

    /// <summary>Remove: id of the AI fill in the fill store (null = not computed; the spot then changes nothing).</summary>
    public string? Fill { get; init; }

    public bool Equals(Spot? other) =>
        other is not null && Id == other.Id && Mode == other.Mode && Center == other.Center && Source == other.Source
        && Radius == other.Radius && Feather == other.Feather && Opacity == other.Opacity && Fill == other.Fill
        && Path.SequenceEqual(other.Path);

    public override int GetHashCode() => HashCode.Combine(Id, Mode, Center, Source, Radius, Fill, Path.Count);

    public const float MinRadius = 0.002f, MaxRadius = 0.25f;

    /// <summary>Values clamped into their ranges (for sidecars written by hand or older versions).</summary>
    public Spot Normalized() => this with
    {
        Center = Clamp01(Center),
        Source = Clamp01(Source),
        Radius = Math.Clamp(float.IsFinite(Radius) ? Radius : 0.02f, MinRadius, MaxRadius),
        Feather = Math.Clamp(float.IsFinite(Feather) ? Feather : 0.5f, 0f, 1f),
        Opacity = Math.Clamp(float.IsFinite(Opacity) ? Opacity : 1f, 0f, 1f),
        Path = (Path ?? []).Select(Clamp01).ToImmutableList(),
    };

    private static BrushPoint Clamp01(BrushPoint p) =>
        new(Math.Clamp(float.IsFinite(p.X) ? p.X : 0.5f, 0f, 1f), Math.Clamp(float.IsFinite(p.Y) ? p.Y : 0.5f, 0f, 1f));
}
