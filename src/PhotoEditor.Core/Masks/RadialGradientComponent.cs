namespace PhotoEditor.Core.Masks;

/// <summary>
/// Elliptical gradient: full effect inside, fading to none at the ellipse edge (use
/// <see cref="MaskComponent.Invert"/> for the outside). Both radii are fractions of the image
/// width, so equal radii give a circle on any aspect ratio.
/// </summary>
public sealed record RadialGradientComponent : MaskComponent
{
    public BrushPoint Center { get; init; } = new(0.5f, 0.5f);
    public float RadiusX { get; init; } = 0.2f;
    public float RadiusY { get; init; } = 0.15f;

    /// <summary>0 = hard edge, 1 = fades from the centre.</summary>
    public float Feather { get; init; } = 0.5f;

    public override string DisplayName => "Radial gradient";

    /// <summary>Coverage at pixel position (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public float CoverageAt(float x, float y, int width, int height)
    {
        float rx = RadiusX * width, ry = RadiusY * width;
        if (rx < 1e-3f || ry < 1e-3f)
            return 0f;
        float dx = (x - Center.X * width) / rx, dy = (y - Center.Y * height) / ry;
        return Profile(MathF.Sqrt(dx * dx + dy * dy), Feather);
    }

    /// <summary>1 inside the unfeathered core, smooth fall-off to 0 at distance 1 (the ellipse).</summary>
    public static float Profile(float distance, float feather)
    {
        float inner = 1f - Math.Clamp(feather, 0f, 1f);
        if (distance >= 1f)
            return 0f;
        if (distance <= inner)
            return 1f;
        float t = (distance - inner) / (1f - inner);
        return 1f - t * t * (3f - 2f * t);
    }

    public override void Render(Span<float> coverage, int width, int height)
    {
        float rx = RadiusX * width, ry = RadiusY * width;
        if (rx < 1e-3f || ry < 1e-3f)
        {
            coverage[..(width * height)].Clear();
            return;
        }
        float cx = Center.X * width, cy = Center.Y * height;
        float irx = 1f / rx, iry = 1f / ry;
        for (int y = 0; y < height; y++)
        {
            float dy = (y + 0.5f - cy) * iry;
            float dy2 = dy * dy;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float dx = (x + 0.5f - cx) * irx;
                coverage[row + x] = Profile(MathF.Sqrt(dx * dx + dy2), Feather);
            }
        }
    }

    /// <summary>Handle positions (normalised) for an image with the given width / height ratio.</summary>
    public IEnumerable<(GradientHandle Handle, BrushPoint Point)> HandlePoints(float aspect)
    {
        float ry = RadiusY * aspect; // RadiusY is in width units; convert to normalised height
        yield return (GradientHandle.Move, Center);
        yield return (GradientHandle.Left, new(Center.X - RadiusX, Center.Y));
        yield return (GradientHandle.Right, new(Center.X + RadiusX, Center.Y));
        yield return (GradientHandle.Top, new(Center.X, Center.Y - ry));
        yield return (GradientHandle.Bottom, new(Center.X, Center.Y + ry));
    }

    /// <summary>
    /// Result of dragging <paramref name="handle"/> from <paramref name="from"/> to <paramref name="to"/>
    /// (normalised; <paramref name="aspect"/> = image width / height). <see cref="GradientHandle.End"/>
    /// sets both radii from the centre (used while creating by dragging).
    /// </summary>
    public RadialGradientComponent DragHandle(GradientHandle handle, BrushPoint from, BrushPoint to, float aspect)
    {
        float radiusX = MathF.Abs(to.X - Center.X);
        float radiusY = MathF.Abs(to.Y - Center.Y) / aspect;
        return handle switch
        {
            GradientHandle.Move => this with { Center = new(Center.X + to.X - from.X, Center.Y + to.Y - from.Y) },
            GradientHandle.Left or GradientHandle.Right => this with { RadiusX = radiusX },
            GradientHandle.Top or GradientHandle.Bottom => this with { RadiusY = radiusY },
            GradientHandle.End => this with { RadiusX = radiusX, RadiusY = radiusY },
            _ => this,
        };
    }
}
