using System.Collections.Immutable;

namespace PhotoEditor.Core.Masks;

/// <summary>A point of a brush stroke in normalised image coordinates.</summary>
public readonly record struct BrushPoint(float X, float Y);

/// <summary>
/// One brush stroke. <see cref="Radius"/> is relative to the image's longer side so strokes look
/// the same at preview and export resolution.
/// </summary>
public sealed record BrushStroke
{
    /// <summary>Radius as a fraction of the longer image side.</summary>
    public float Radius { get; init; } = 0.02f;

    /// <summary>0 = hard edge, 1 = soft from the centre.</summary>
    public float Feather { get; init; } = 0.5f;

    /// <summary>How much one stroke adds (0..1); strokes build up.</summary>
    public float Flow { get; init; } = 1f;

    /// <summary>Removes coverage instead of adding it.</summary>
    public bool Erase { get; init; }

    public ImmutableList<BrushPoint> Points { get; init; } = [];

    public bool Equals(BrushStroke? other) =>
        other is not null && Radius == other.Radius && Feather == other.Feather && Flow == other.Flow
        && Erase == other.Erase && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(Radius, Feather, Flow, Erase, Points.Count);
}

/// <summary>Painted mask: strokes applied in order.</summary>
public sealed record BrushComponent : MaskComponent
{
    public ImmutableList<BrushStroke> Strokes { get; init; } = [];

    public override string DisplayName => "Brush";

    public BrushComponent AddStroke(BrushStroke stroke) => this with { Strokes = Strokes.Add(stroke) };

    /// <summary>Appends a point to the last stroke.</summary>
    public BrushComponent ExtendLastStroke(BrushPoint point) =>
        Strokes.Count == 0
            ? this
            : this with { Strokes = Strokes.SetItem(Strokes.Count - 1, Strokes[^1] with { Points = Strokes[^1].Points.Add(point) }) };

    public override void Render(Span<float> coverage, int width, int height)
    {
        coverage.Clear();
        if (Strokes.Count == 0)
            return;
        // Scratch buffer per thread, kept all-zero between strokes (each stroke clears what it used).
        if (_scratch is null || _scratch.Length < width * height)
            _scratch = new float[width * height];
        foreach (var stroke in Strokes)
            RenderStroke(stroke, coverage, _scratch, width, height);
    }

    [ThreadStatic]
    private static float[]? _scratch;

    /// <summary>Soft round dab profile: 1 inside the hard core, smooth fall-off to 0 at the radius.</summary>
    public static float Falloff(float distance, float feather)
    {
        if (distance >= 1f)
            return 0f;
        float hard = 1f - Math.Clamp(feather, 0f, 1f);
        if (distance <= hard)
            return 1f;
        float t = (distance - hard) / (1f - hard);
        return 1f - t * t * (3f - 2f * t);
    }

    private static void RenderStroke(BrushStroke stroke, Span<float> coverage, float[] buffer, int width, int height)
    {
        if (stroke.Points.Count == 0)
            return;
        float radius = MathF.Max(0.5f, stroke.Radius * Math.Max(width, height));

        // The stroke's own coverage is the maximum over its segments (a round brush swept along the
        // polyline), so overlapping parts of one stroke don't build up.
        int minX = width, minY = height, maxX = -1, maxY = -1;
        void Segment(float ax, float ay, float bx, float by)
        {
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, bx) - radius));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(ax, bx) + radius));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, by) - radius));
            int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(ay, by) + radius));
            if (x0 > x1 || y0 > y1)
                return;
            minX = Math.Min(minX, x0); maxX = Math.Max(maxX, x1);
            minY = Math.Min(minY, y0); maxY = Math.Max(maxY, y1);
            float ex = bx - ax, ey = by - ay;
            float lengthSq = ex * ex + ey * ey;
            float invLengthSq = lengthSq > 1e-6f ? 1f / lengthSq : 0f;
            float invRadius = 1f / radius;
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f - ay;
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f - ax;
                    float t = Math.Clamp((px * ex + py * ey) * invLengthSq, 0f, 1f);
                    float dx = px - t * ex, dy = py - t * ey;
                    float distSq = dx * dx + dy * dy;
                    if (distSq >= radius * radius)
                        continue;
                    float f = Falloff(MathF.Sqrt(distSq) * invRadius, stroke.Feather);
                    if (f > buffer[row + x])
                        buffer[row + x] = f;
                }
            }
        }

        float lx = stroke.Points[0].X * width, ly = stroke.Points[0].Y * height;
        if (stroke.Points.Count == 1)
            Segment(lx, ly, lx, ly);
        for (int i = 1; i < stroke.Points.Count; i++)
        {
            float nx = stroke.Points[i].X * width, ny = stroke.Points[i].Y * height;
            Segment(lx, ly, nx, ny);
            lx = nx;
            ly = ny;
        }

        float flow = Math.Clamp(stroke.Flow, 0f, 1f);
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * width;
            for (int x = minX; x <= maxX; x++)
            {
                float a = flow * buffer[row + x];
                ref float c = ref coverage[row + x];
                c = stroke.Erase ? c * (1f - a) : c + (1f - c) * a;
                buffer[row + x] = 0f;
            }
        }
    }

    public bool Equals(BrushComponent? other) =>
        other is not null && base.Equals(other) && Strokes.SequenceEqual(other.Strokes);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Strokes.Count);
}
