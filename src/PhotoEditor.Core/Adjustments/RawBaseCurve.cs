namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// The base rendering of RAW photos (applied when they are decoded): scene-linear, 1 = where a neutral clips on the
/// sensor, to display-linear. A contrast power on the brightened scene value (<see cref="Exposure"/>,
/// <see cref="Contrast"/>), then a highlight shoulder (extended Reinhard) reaching white at <see cref="WhiteScene"/>;
/// brighter (rebuilt) values continue above white, into the headroom. Like Lightroom, the Exposure slider works
/// before this curve on such photos (<see cref="ApplyExposure"/>): brightening moves the highlights along the
/// shoulder instead of pushing them past white.
/// <para>
/// Fitted (<c>Bench/ProfileFitBench</c>) to Lightroom's default rendering of the Canon R8 sample (16-bit TIFF export,
/// Exposure 0, image centre), through the hue-preserving rendering (the curve acts on the brightest and darkest
/// channel, so a saturated colour renders brighter than its luminance alone would): the luminance of the result is
/// within 2.8 sRGB levels RMS of Lightroom's from 10 stops under the clip to above it. Lightroom's rendering is an
/// S-curve: deep shadows near black, steep midtones, a strong shoulder. The previous curve, fitted to the camera JPEG's
/// midtones, was 28 levels off and read as flat and soft next to Lightroom: matched in tone, both renders have the
/// same detail at every scale.
/// </para>
/// </summary>
public static class RawBaseCurve
{
    /// <summary>Gain on the scene value before the contrast power.</summary>
    public const float Exposure = 4.989f;

    /// <summary>Contrast: power on the brightened scene value (1 = none); the shadows fall towards black with it.</summary>
    public const float Contrast = 2.02f;

    /// <summary>
    /// The scene value (1 = where a neutral clips on the sensor) at which the highlight shoulder reaches display white.
    /// Above 1 so that what the highlight rebuild reconstructs beyond the sensor's clip (bright clouds) still has
    /// gradation under white instead of being clipped flat at once.
    /// </summary>
    public const float WhiteScene = 4.033f;

    /// <summary>(Exposure · WhiteScene)^Contrast, the shoulder's white in the curve's own units (shader constant).</summary>
    public static readonly float WhiteZ = MathF.Pow(Exposure * WhiteScene, Contrast);

    public static float Apply(float x)
    {
        float z = MathF.Pow(MathF.Max(x, 0) * Exposure, Contrast);
        return z * (1 + z / (WhiteZ * WhiteZ)) / (1 + z);
    }

    /// <summary>Inverse of <see cref="Apply"/> (the shoulder solved as a quadratic, in the stable form).</summary>
    public static float Invert(float y)
    {
        y = MathF.Max(y, 0);
        float z = 2 * y / (1 - y + MathF.Sqrt((1 - y) * (1 - y) + 4 * y / (WhiteZ * WhiteZ)));
        return MathF.Pow(z, 1 / Contrast) / Exposure;
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

    /// <summary>
    /// A brightness factor (<paramref name="gain"/>, display-linear: darkened highlights) on a pixel of a photo rendered
    /// with this curve, with the colour the darkening brings back when it is done on the scene values: near white the
    /// shoulder has pressed colours together (Lightroom's −95 Highlights turns the R8 sample's near-white sky into a
    /// saturated blue, with the scene's own hue), a plain factor keeps them pale. The colour change is worked out at the
    /// luminance the plain factor gives and added to the plainly darkened pixel, so tone and detail stay as before.
    /// Undoing the shoulder enlarges colour differences near white up to 20 times, so it is worked out twice, on
    /// <paramref name="r0"/> … (the same pixel before sharpening and the other detail filters: per-channel sharpening
    /// made speckle) and on <paramref name="rn"/> … (the neighbourhood's colour), and only what both agree on is
    /// applied: one- or two-pixel colour fringes along edges (lateral chromatic aberration, demosaicing: cyan on one
    /// side of a leaf, red on the other) cancel out in the neighbourhood and stay as they were; an even sky or cloud
    /// gets its full colour.
    /// </summary>
    public static void SceneColour(ref float r, ref float g, ref float b, float gain, float r0, float g0, float b0,
        float rn, float gn, float bn)
    {
        var (dr, dg, db) = SceneColourShift(gain, r0, g0, b0);
        var (nr, ng, nb) = SceneColourShift(gain, rn, gn, bn);
        r = MathF.Max(r * gain + Agreed(dr, nr), 0f);
        g = MathF.Max(g * gain + Agreed(dg, ng), 0f);
        b = MathF.Max(b * gain + Agreed(db, nb), 0f);
    }

    /// <summary>The part of two changes both agree on: the smaller one where they have the same sign, else none.</summary>
    private static float Agreed(float a, float b) => a > 0 && b > 0 ? MathF.Min(a, b) : a < 0 && b < 0 ? MathF.Max(a, b) : 0f;

    /// <summary>Darkening on the scene values at the plain darkening's luminance, minus the plain darkening.</summary>
    private static (float R, float G, float B) SceneColourShift(float gain, float r0, float g0, float b0)
    {
        float y0 = ToneCurve.Luminance(r0, g0, b0);
        float sr = r0, sg = g0, sb = b0;
        if (y0 > 1e-6f)
        {
            float gs = Invert(y0 * gain) / Invert(y0);
            ApplyScene(ref sr, ref sg, ref sb, gs, gs, gs);
        }
        float ys = ToneCurve.Luminance(sr, sg, sb);
        float k = ys > 1e-9f ? y0 * gain / ys : gain;
        return (sr * k - r0 * gain, sg * k - g0 * gain, sb * k - b0 * gain);
    }
}