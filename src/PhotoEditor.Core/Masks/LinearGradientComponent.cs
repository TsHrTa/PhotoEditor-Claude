namespace PhotoEditor.Core.Masks;

/// <summary>
/// Linear gradient: full effect on the start side, fading smoothly to none at the end line.
/// Points are in normalised image coordinates.
/// </summary>
public sealed record LinearGradientComponent : MaskComponent
{
    public BrushPoint Start { get; init; } = new(0.5f, 0.25f);
    public BrushPoint End { get; init; } = new(0.5f, 0.5f);

    public override string DisplayName => "Linear gradient";

    public BrushPoint Center => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2);

    /// <summary>Result of dragging <paramref name="handle"/> from <paramref name="from"/> to <paramref name="to"/> (normalised).</summary>
    public LinearGradientComponent DragHandle(GradientHandle handle, BrushPoint from, BrushPoint to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        return handle switch
        {
            GradientHandle.Start => this with { Start = new(Start.X + dx, Start.Y + dy) },
            GradientHandle.End => this with { End = new(End.X + dx, End.Y + dy) },
            GradientHandle.Move => this with { Start = new(Start.X + dx, Start.Y + dy), End = new(End.X + dx, End.Y + dy) },
            _ => this,
        };
    }

    /// <summary>Coverage at pixel centre (<paramref name="x"/>, <paramref name="y"/>) in pixels.</summary>
    public float CoverageAt(float x, float y, int width, int height)
    {
        float ax = Start.X * width, ay = Start.Y * height;
        float dx = End.X * width - ax, dy = End.Y * height - ay;
        float lengthSq = dx * dx + dy * dy;
        if (lengthSq < 1e-6f)
            return 0f;
        float t = ((x - ax) * dx + (y - ay) * dy) / lengthSq;
        return 1f - ToneSmoothStep(t);
    }

    public override void Render(Span<float> coverage, int width, int height)
    {
        float ax = Start.X * width, ay = Start.Y * height;
        float dx = End.X * width - ax, dy = End.Y * height - ay;
        float lengthSq = dx * dx + dy * dy;
        if (lengthSq < 1e-6f)
        {
            coverage[..(width * height)].Clear();
            return;
        }
        float kx = dx / lengthSq, ky = dy / lengthSq;
        for (int y = 0; y < height; y++)
        {
            float py = (y + 0.5f - ay) * ky;
            int row = y * width;
            for (int x = 0; x < width; x++)
                coverage[row + x] = 1f - ToneSmoothStep((x + 0.5f - ax) * kx + py);
        }
    }

    private static float ToneSmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
