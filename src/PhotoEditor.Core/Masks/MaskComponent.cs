namespace PhotoEditor.Core.Masks;

/// <summary>How a component's coverage is combined with the components before it.</summary>
public enum MaskMode
{
    /// <summary>Union: max(mask, component).</summary>
    Add,

    /// <summary>mask × (1 − component).</summary>
    Subtract,

    /// <summary>mask × component.</summary>
    Intersect,
}

/// <summary>
/// One shape of a mask (brush, gradient, …). Geometry is stored in normalised image
/// coordinates (0..1 across width and height) so it works at any resolution.
/// </summary>
/// <remarks>
/// Serialised polymorphically with a "type" discriminator; every concrete component must be
/// registered here with [JsonDerivedType] (System.Text.Json requires at least one).
/// </remarks>
public abstract record MaskComponent
{
    public MaskMode Mode { get; init; } = MaskMode.Add;

    /// <summary>Uses 1 − coverage.</summary>
    public bool Invert { get; init; }

    /// <summary>
    /// Writes this component's coverage (0..1) for a <paramref name="width"/> × <paramref name="height"/>
    /// raster into <paramref name="coverage"/> (row-major), overwriting every value. Ignores <see cref="Invert"/>.
    /// </summary>
    public abstract void Render(Span<float> coverage, int width, int height);
}
