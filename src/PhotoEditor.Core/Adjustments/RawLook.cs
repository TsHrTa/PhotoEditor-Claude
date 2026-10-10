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
        (260, 3, 0.75f),
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

    /// <summary>Applies the look to a display-linear sRGB colour (values above white allowed); greys are unchanged.</summary>
    public static void Apply(ref float r, ref float g, ref float b)
    {
        var (l, ca, cb) = HslMath.ToOklab(r, g, b);
        float chroma = MathF.Sqrt(ca * ca + cb * cb);
        if (chroma < 1e-5f)
            return;
        var (shift, factor) = At(MathF.Atan2(cb, ca) * (180f / MathF.PI));
        float angle = shift * (MathF.PI / 180f);
        float cos = MathF.Cos(angle) * factor, sin = MathF.Sin(angle) * factor;
        (r, g, b) = GamutMapping.FromOklab(l, ca * cos - cb * sin, ca * sin + cb * cos);
    }
}
