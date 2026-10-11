namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// The colour part of the RAW base rendering, after <see cref="RawBaseCurve"/> (baked in at decode, like a camera
/// profile's look table): a hue shift and a chroma factor by OKLCh hue, at constant OKLab lightness. Fitted
/// (<c>Bench/ProfileFitBench</c>, 8 × 8 pixel blocks aligned to the Lightroom TIFF) to Lightroom's default rendering of
/// the Canon R8 sample: Lightroom turns yellow foliage about 10 to 14 degrees towards orange, and the hue-preserving base
/// curve leaves colours a little more chromatic than Lightroom's (yellows and greens × 0.95, a blue sky × 0.75).
/// Hues the sample has few pixels of keep the average factor and no shift.
/// </summary>
public static class RawLook
{
    /// <summary>(OKLCh hue in degrees, hue shift in degrees, chroma factor), periodic, linearly interpolated.</summary>
    public static readonly (float Hue, float Shift, float Chroma)[] Points =
    [
        (45, 0, 0.95f),
        (70, -14, 0.97f),
        (85, -13, 0.97f),
        (100, -7, 0.95f),
        (115, 3, 0.94f),
        (140, 0, 0.94f),
        (220, 0, 0.85f),
        (268, -20, 0.85f),
        (310, 0, 0.93f),
    ];

    /// <summary>The look's hue shift (degrees) and chroma factor at OKLCh <paramref name="hue"/> (degrees).</summary>
    public static (float Shift, float Chroma) At(float hue)
    {
        hue = ((hue % 360) + 360) % 360;
        int n = Points.Length;
        for (int i = 0; i < n; i++)
        {
            var a = Points[i];
            var b = Points[(i + 1) % n];
            float span = ((b.Hue - a.Hue) % 360 + 360) % 360;
            float d = ((hue - a.Hue) % 360 + 360) % 360;
            if (d <= span)
            {
                float t = span > 0 ? d / span : 0;
                return (a.Shift + (b.Shift - a.Shift) * t, a.Chroma + (b.Chroma - a.Chroma) * t);
            }
        }
        return (0, 1);
    }

    /// <summary>OKLCh hues (degrees) of the rows of <see cref="LightShift"/> / <see cref="LightChroma"/> (hue after the base shift).</summary>
    public static readonly float[] LightHues = [52.5f, 67.5f, 82.5f, 97.5f, 112.5f, 127.5f];

    /// <summary>OKLab lightness of the columns (0.25 to 0.85).</summary>
    public const float LightFirst = 0.25f, LightStep = 0.1f;

    /// <summary>
    /// Extra hue shift (degrees) by hue and lightness, on top of <see cref="Points"/>. Lightroom's profile depends on
    /// brightness: sunlit yellow leaves stay a clean yellow, shaded ones turn darker orange, so neighbouring leaves differ
    /// in colour as well as brightness (measured, <c>Bench/ProfileFitBench</c> by hue and lightness: after the hue-only
    /// look, Lightroom's bright yellows were 7 to 13 degrees yellower and its mid-dark ones 3 to 5 degrees more orange).
    /// Without it foliage was one ochre and read as flat and soft next to Lightroom although its detail measured the same.
    /// Cells with few pixels are 0.
    /// </summary>
    public static readonly float[,] LightShift =
    {
        { 0.8f, 0.2f, 2.0f, 7.5f, 12.2f, 14.7f, 14.7f },
        { -2.0f, -3.6f, -4.5f, 0.6f, 6.7f, 12.7f, 11.8f },
        { 1.7f, 0.0f, -4.4f, -3.0f, 1.9f, 6.9f, 9.7f },
        { 4.1f, 2.1f, -3.5f, -5.2f, -2.4f, 0.4f, 1.0f },
        { 7.0f, 2.2f, -4.3f, -7.0f, -6.5f, -8.5f, -8.5f },
        { 0f, 0f, 0f, 0f, 0f, 0f, 0f },
    };

    /// <summary>Extra chroma factor by hue and lightness, on top of <see cref="Points"/> (same grid as <see cref="LightShift"/>).</summary>
    public static readonly float[,] LightChroma =
    {
        { 1.03f, 1.04f, 1.05f, 0.99f, 0.88f, 0.75f, 0.75f },
        { 0.99f, 0.99f, 1.05f, 1.05f, 1.02f, 1.01f, 0.76f },
        { 0.92f, 0.93f, 0.99f, 1.03f, 1.02f, 1.01f, 0.99f },
        { 0.95f, 0.96f, 0.99f, 1.03f, 1.04f, 1.02f, 0.94f },
        { 0.98f, 1.01f, 1.02f, 1.03f, 1.01f, 0.90f, 0.90f },
        { 1f, 1f, 1f, 1f, 1f, 1f, 1f },
    };

    /// <summary>
    /// The extra hue shift and chroma factor at <paramref name="hue"/> (after the base shift) and OKLab
    /// <paramref name="lightness"/>: bilinear in the grid, lightness clamped, fading to none half a row outside the hues.
    /// </summary>
    public static (float Shift, float Chroma) ByLightness(float hue, float lightness)
    {
        hue = ((hue % 360) + 360) % 360;
        float rowStep = LightHues[1] - LightHues[0];
        float u = (hue - LightHues[0]) / rowStep;
        int rows = LightHues.Length, cols = LightShift.GetLength(1);
        if (u <= -1f || u >= rows)
            return (0, 1);
        float v = Math.Clamp((lightness - LightFirst) / LightStep, 0f, cols - 1);
        int c0 = Math.Min((int)v, cols - 2);
        float fv = v - c0;
        (float, float) Row(int r) => r < 0 || r >= rows
            ? (0f, 1f)
            : (LightShift[r, c0] + (LightShift[r, c0 + 1] - LightShift[r, c0]) * fv,
               LightChroma[r, c0] + (LightChroma[r, c0 + 1] - LightChroma[r, c0]) * fv);
        int r0 = (int)MathF.Floor(u);
        float fu = u - r0;
        var (s0, k0) = Row(r0);
        var (s1, k1) = Row(r0 + 1);
        return (s0 + (s1 - s0) * fu, k0 + (k1 - k0) * fu);
    }

    /// <summary>Applies the look to a display-linear sRGB colour (values above white allowed); greys are unchanged.</summary>
    public static void Apply(ref float r, ref float g, ref float b)
    {
        var (l, ca, cb) = HslMath.ToOklab(r, g, b);
        float chroma = MathF.Sqrt(ca * ca + cb * cb);
        if (chroma < 1e-5f)
            return;
        float hue = MathF.Atan2(cb, ca) * (180f / MathF.PI);
        var (shift, factor) = At(hue);
        var (extraShift, extraFactor) = ByLightness(hue + shift, l);
        shift += extraShift;
        factor *= extraFactor;
        float angle = shift * (MathF.PI / 180f);
        float cos = MathF.Cos(angle) * factor, sin = MathF.Sin(angle) * factor;
        (r, g, b) = GamutMapping.FromOklab(l, ca * cos - cb * sin, ca * sin + cb * cos);
    }
}
