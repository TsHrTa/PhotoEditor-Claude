namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// The base rendering of RAW photos (applied when they are decoded): scene-linear, 1 = where a neutral clips on the
/// sensor, to display-linear. The midtones are brightened (<see cref="Exposure"/>), the deepest shadows get a toe and
/// the highlights roll off smoothly (extended Reinhard), reaching white exactly at the sensor's white, so bright
/// clouds keep their shading; brighter (rebuilt) values continue above white, into the headroom. Like Lightroom,
/// the Exposure slider works before this curve on such photos (<see cref="ApplyExposure"/>): brightening moves the
/// highlights along the shoulder instead of pushing them past white.
/// </summary>
public static class RawBaseCurve
{
    /// <summary>
    /// Brightening of the midtones, fitted to the camera's own JPEG (Canon R8, standard picture style) — what
    /// Lightroom's "Camera Standard" profile imitates: 4 stops under the sensor's white becomes ≈ 20 % grey
    /// (sRGB ≈ 124).
    /// </summary>
    public const float Exposure = 4.3f;

    /// <summary>
    /// The toe: the deepest shadows are darkened (≈ −0.5 stop at 2 % grey, −1.5 at 0.5 %), as the camera's and
    /// Adobe's default curves do, so blacks are black instead of a grey veil of sensor noise.
    /// </summary>
    public const float Toe = 0.012f;

    public static float Apply(float x)
    {
        float z = MathF.Max(x, 0) * Exposure;
        float y = z * (1 + z / (Exposure * Exposure)) / (1 + z);
        return y * y * (1 + Toe) / (y + Toe);
    }

    /// <summary>Inverse of <see cref="Apply"/> (both steps solved as quadratics, in the stable form).</summary>
    public static float Invert(float y)
    {
        y = MathF.Max(y, 0);
        float u = (y + MathF.Sqrt(y * y + 4 * (1 + Toe) * Toe * y)) / (2 * (1 + Toe));
        float z = 2 * u / (1 - u + MathF.Sqrt((1 - u) * (1 - u) + 4 * u / (Exposure * Exposure)));
        return z / Exposure;
    }

    /// <summary>
    /// Exposure (x<paramref name="gain"/>) on a display-linear pixel of a photo rendered with this curve, as if it
    /// were applied before the curve (see <see cref="ApplyScene"/>).
    /// </summary>
    public static void ApplyExposure(ref float r, ref float g, ref float b, float gain) =>
        ApplyScene(ref r, ref g, ref b, gain, gain, gain);

    /// <summary>
    /// Per-channel gains (white balance, exposure) on a display-linear pixel of a photo rendered with this curve, applied
    /// where they belong, on the scene-linear values before the curve: the rendering is undone (the brightest and darkest
    /// channels through <see cref="Invert"/>, the other one keeps its place between them), the gains are applied, and the
    /// rendering is done again with the same hue-preserving rule. A white balance then moves the highlights along the
    /// shoulder (they roll off towards white) instead of tinting what is already near white, as it would on
    /// display values.
    /// </summary>
    public static void ApplyScene(ref float r, ref float g, ref float b, float gainR, float gainG, float gainB)
    {
        float hi = MathF.Max(r, MathF.Max(g, b)), lo = MathF.Max(MathF.Min(r, MathF.Min(g, b)), 0);
        if (hi <= 0)
            return;
        float hs = Invert(hi), ls = Invert(lo);
        float sr, sg, sb;
        if (hi - lo < 1e-7f)
            sr = sg = sb = hs;
        else
        {
            float k = (hs - ls) / (hi - lo);
            sr = ls + (MathF.Max(r, 0) - lo) * k;
            sg = ls + (MathF.Max(g, 0) - lo) * k;
            sb = ls + (MathF.Max(b, 0) - lo) * k;
        }
        sr *= gainR;
        sg *= gainG;
        sb *= gainB;
        float hi2 = MathF.Max(sr, MathF.Max(sg, sb)), lo2 = MathF.Min(sr, MathF.Min(sg, sb));
        float h = Apply(hi2), l = Apply(lo2);
        if (hi2 - lo2 < 1e-9f)
        {
            r = g = b = h;
            return;
        }
        float m = (h - l) / (hi2 - lo2);
        r = l + (sr - lo2) * m;
        g = l + (sg - lo2) * m;
        b = l + (sb - lo2) * m;
    }
}