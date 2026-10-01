namespace PhotoEditor.Core.Adjustments;

/// <summary>Hue / saturation / luminance adjustment (-100..+100 each) for one colour band.</summary>
public sealed record HslBand(double Hue = 0, double Saturation = 0, double Luminance = 0)
{
    public static readonly HslBand Zero = new();
}

/// <summary>The eight colour bands of the HSL panel.</summary>
public static class HslBands
{
    public const int Count = 8;

    public static readonly string[] Names = ["Reds", "Oranges", "Yellows", "Greens", "Aquas", "Blues", "Purples", "Magentas"];

    /// <summary>The band centres as the usual HSL panel names them (HSV hue, degrees).</summary>
    private static readonly float[] HsvCenters = [0, 30, 60, 120, 180, 240, 270, 300];

    /// <summary>
    /// Hue (degrees, in OKLCh, where the panel works) at which each band has full effect; between centres the effect is
    /// blended. They are the OKLCh hues of the fully saturated colours at the HSV centres, so a colour belongs to the
    /// band a user would call it (red 29, orange 56, yellow 110, green 142, aqua 195, blue 264, purple 292, magenta 328).
    /// </summary>
    public static readonly float[] Centers = HsvCenters.Select(h =>
    {
        var (r, g, b) = HslMath.HsvToRgb(h, 1f, 1f);
        return HslMath.Oklch(ColorMath.SrgbToLinear(r), ColorMath.SrgbToLinear(g), ColorMath.SrgbToLinear(b)).Hue;
    }).ToArray();

    /// <summary>Centres relative to the first (red) band, from 0 up to below 360 and increasing: what <see cref="HslMath.Interpolate"/> works on.</summary>
    public static readonly float[] RelativeCenters = Centers.Select(c => c - Centers[0]).ToArray();

    /// <summary>Hue rotation in degrees at ±100.</summary>
    public const float MaxHueShift = 30f;

    /// <summary>Luminance change in stops at ±100 (for fully saturated colours).</summary>
    public const float MaxLuminanceStops = 1.5f;

    public static HslBand Get(AdjustmentSettings s, int band) => band switch
    {
        0 => s.Reds,
        1 => s.Oranges,
        2 => s.Yellows,
        3 => s.Greens,
        4 => s.Aquas,
        5 => s.Blues,
        6 => s.Purples,
        7 => s.Magentas,
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    public static AdjustmentSettings With(AdjustmentSettings s, int band, HslBand value) => band switch
    {
        0 => s with { Reds = value },
        1 => s with { Oranges = value },
        2 => s with { Yellows = value },
        3 => s with { Greens = value },
        4 => s with { Aquas = value },
        5 => s with { Blues = value },
        6 => s with { Purples = value },
        7 => s with { Magentas = value },
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };
}
