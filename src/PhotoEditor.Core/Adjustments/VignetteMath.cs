namespace PhotoEditor.Core.Adjustments;

/// <summary>Post-crop vignette (relative to the crop frame). Mirrored in <see cref="AdjustmentShader"/>.</summary>
public static class VignetteMath
{
    /// <summary>
    /// Distance from the centre for (<paramref name="u"/>, <paramref name="v"/>) in -1..1 across the image
    /// (1 at the corners). Roundness 0 = ellipse following the frame, +1 = circle, -1 = nearly rectangular.
    /// </summary>
    public static float Distance(float u, float v, float width, float height, float roundness)
    {
        if (roundness >= 0f)
        {
            float ellipse = MathF.Sqrt(u * u + v * v) / MathF.Sqrt(2f);
            float circle = MathF.Sqrt(u * u * width * width + v * v * height * height) / MathF.Sqrt(width * width + height * height);
            return ellipse + (circle - ellipse) * roundness;
        }
        float p = 2f - 6f * roundness;
        return MathF.Pow(MathF.Pow(MathF.Abs(u), p) + MathF.Pow(MathF.Abs(v), p), 1f / p) / MathF.Pow(2f, 1f / p);
    }

    /// <summary>0 in the centre, 1 where the vignette has full effect.</summary>
    public static float Weight(float u, float v, float width, float height, in PreparedAdjustments p) =>
        ToneCurve.SmoothStep(p.VignetteLow, p.VignetteHigh, Distance(u, v, width, height, p.VignetteRoundness));

    /// <summary>
    /// The rectangle the vignette follows (the crop frame) in pixels of the rendered image, with the
    /// rotation precomputed. Mirrored by the shader's <c>vignetteCenter / vignetteHalf / vignetteRotation</c>.
    /// </summary>
    public readonly record struct Frame(float CenterX, float CenterY, float HalfWidth, float HalfHeight, float Cos, float Sin)
    {
        public static Frame Full(float width, float height) => new(width / 2, height / 2, width / 2, height / 2, 1, 0);

        public static Frame From(Editing.CropFrame f) =>
            new((float)f.CenterX, (float)f.CenterY, (float)f.HalfWidth, (float)f.HalfHeight, (float)f.Cos, (float)f.Sin);
    }

    /// <summary>Applies the vignette (as an exposure change) to linear RGB at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void Apply(ref float r, ref float g, ref float b, float x, float y, in Frame frame, in PreparedAdjustments p)
    {
        float dx = x + 0.5f - frame.CenterX, dy = y + 0.5f - frame.CenterY;
        float u = (dx * frame.Cos + dy * frame.Sin) / frame.HalfWidth;
        float v = (-dx * frame.Sin + dy * frame.Cos) / frame.HalfHeight;
        float gain = MathF.Pow(2f, p.VignetteStops * Weight(u, v, frame.HalfWidth * 2, frame.HalfHeight * 2, p));
        r *= gain;
        g *= gain;
        b *= gain;
    }
}
