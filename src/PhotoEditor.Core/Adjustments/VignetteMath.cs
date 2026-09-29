namespace PhotoEditor.Core.Adjustments;

/// <summary>Post-crop style vignette. Mirrored in <see cref="AdjustmentShader"/>.</summary>
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

    /// <summary>Applies the vignette (as an exposure change) to linear RGB at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void Apply(ref float r, ref float g, ref float b, float x, float y, float width, float height, in PreparedAdjustments p)
    {
        float u = ((x + 0.5f) / width - 0.5f) * 2f, v = ((y + 0.5f) / height - 0.5f) * 2f;
        float gain = MathF.Pow(2f, p.VignetteStops * Weight(u, v, width, height, p));
        r *= gain;
        g *= gain;
        b *= gain;
    }
}
