using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public class HslMathTests
{
    [Theory]
    [InlineData(1f, 0f, 0f, 0f)]
    [InlineData(1f, 1f, 0f, 60f)]
    [InlineData(0f, 1f, 0f, 120f)]
    [InlineData(0f, 0f, 1f, 240f)]
    [InlineData(1f, 0f, 1f, 300f)]
    public void RgbToHsv_Hue(float r, float g, float b, float hue) =>
        Assert.Equal(hue, HslMath.RgbToHsv(r, g, b).H, 3);

    [Fact]
    public void HsvRoundTrip()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            float r = rnd.NextSingle(), g = rnd.NextSingle(), b = rnd.NextSingle();
            var (h, s, v) = HslMath.RgbToHsv(r, g, b);
            var (r2, g2, b2) = HslMath.HsvToRgb(h, s, v);
            Assert.Equal(r, r2, 1e-4f);
            Assert.Equal(g, g2, 1e-4f);
            Assert.Equal(b, b2, 1e-4f);
        }
    }

    [Fact]
    public void Interpolate_FullEffectAtCentre_BlendsBetween()
    {
        var c = HslBands.Centers;
        var p = PreparedAdjustments.From(new AdjustmentSettings { Greens = new HslBand(0, 100, 0) });
        Assert.Equal(2f, HslMath.Interpolate(c[3], p.Hsl).SatFactor, 4);
        float halfway = (c[3] + c[4]) / 2;
        Assert.Equal(1.5f, HslMath.Interpolate(halfway, p.Hsl).SatFactor, 4); // halfway to aquas
        Assert.Equal(1f, HslMath.Interpolate(c[4] + 5, p.Hsl).SatFactor, 1);
        Assert.Equal(1f, HslMath.Interpolate(c[4], p.Hsl).SatFactor, 4);
    }

    [Fact]
    public void Interpolate_WrapsAroundBetweenMagentasAndReds()
    {
        var c = HslBands.Centers;
        var p = PreparedAdjustments.From(new AdjustmentSettings { Reds = new HslBand(100, 0, 0) });
        Assert.Equal(HslBands.MaxHueShift, HslMath.Interpolate(c[0], p.Hsl).HueShift, 4);
        // Between the magentas and the reds (across 0 / 360 degrees) the effect fades in.
        float across = c[7] + (c[0] + 360 - c[7]) / 2;
        Assert.Equal(HslBands.MaxHueShift / 2, HslMath.Interpolate(across % 360, p.Hsl).HueShift, 3);
        Assert.Equal(0f, HslMath.Interpolate(c[7], p.Hsl).HueShift, 4);
    }

    [Fact]
    public void TheBandCentresAreTheOklchHuesOfTheHsvColours_InIncreasingOrder()
    {
        var c = HslBands.Centers;
        Assert.Equal(8, c.Length);
        Assert.InRange(c[0], 20f, 40f);   // red
        Assert.InRange(c[1], 45f, 70f);   // orange
        Assert.InRange(c[2], 100f, 120f); // yellow
        Assert.InRange(c[3], 130f, 150f); // green
        Assert.InRange(c[4], 185f, 205f); // aqua
        Assert.InRange(c[5], 255f, 275f); // blue
        Assert.InRange(c[6], 280f, 310f); // purple
        Assert.InRange(c[7], 320f, 340f); // magenta
        for (int i = 1; i < c.Length; i++)
            Assert.True(c[i] > c[i - 1]);
        Assert.Equal(0f, HslBands.RelativeCenters[0]);
    }

    [Fact]
    public void OklabRoundTrip()
    {
        var rnd = new Random(11);
        for (int i = 0; i < 1000; i++)
        {
            float r = rnd.NextSingle() * 2, g = rnd.NextSingle() * 2, b = rnd.NextSingle() * 2;
            var (l, a, bb) = HslMath.ToOklab(r, g, b);
            var (r2, g2, b2) = HslMath.FromOklab(l, a, bb);
            Assert.Equal(r, r2, 1e-3f);
            Assert.Equal(g, g2, 1e-3f);
            Assert.Equal(b, b2, 1e-3f);
        }
    }

    [Fact]
    public void AHueMove_KeepsTheLightnessOfTheColour()
    {
        // Moving blue towards cyan used to change its brightness (HSV on gamma values); in OKLCh the lightness stays.
        var p = PreparedAdjustments.From(new AdjustmentSettings { Blues = new HslBand(-100, 0, 0) });
        foreach (var (r, g, b) in new[] { (0.05f, 0.1f, 0.6f), (0.1f, 0.3f, 0.8f), (0.6f, 0.2f, 0.1f) })
        {
            float l0 = HslMath.Oklch(r, g, b).Lightness;
            float r2 = r, g2 = g, b2 = b;
            HslMath.Apply(ref r2, ref g2, ref b2, p.Hsl);
            float l1 = HslMath.Oklch(r2, g2, b2).Lightness;
            Assert.InRange(Math.Abs(l1 - l0), 0f, 0.01f);
        }
        // ... and the hue really moved for a blue.
        float rb = 0.05f, gb = 0.1f, bb2 = 0.6f;
        float h0 = HslMath.Oklch(rb, gb, bb2).Hue;
        HslMath.Apply(ref rb, ref gb, ref bb2, p.Hsl);
        Assert.True(HslMath.Oklch(rb, gb, bb2).Hue < h0 - 10f);
    }

    [Fact]
    public void ASaturationBoostOfASaturatedColour_StaysInGamutAndKeepsItsHue()
    {
        var p = PreparedAdjustments.From(new AdjustmentSettings { Reds = new HslBand(0, 100, 0) });
        float r = 0.9f, g = 0.02f, b = 0.01f;
        float hue = HslMath.Oklch(r, g, b).Hue;
        HslMath.Apply(ref r, ref g, ref b, p.Hsl);
        Assert.True(MathF.Min(r, MathF.Min(g, b)) >= -1e-6f);
        Assert.InRange(Math.Abs(HslMath.Oklch(r, g, b).Hue - hue), 0f, 4f); // compressed towards luminance: hue nearly kept
    }

    [Theory]
    [InlineData(0, 0, 255)]
    [InlineData(0, 15, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(255, 255, 0)]
    public void AColourOnTheEdgeOfSrgb_IsNotPulledInByABandItDoesNotBelongTo(int r, int g, int b)
    {
        // The line from the blue primary towards grey at constant OKLab hue leaves sRGB for a moment; the gamut mapping
        // must not mistake the primary itself for out of gamut and give up 10 % of its chroma.
        var s = new AdjustmentSettings { Magentas = new HslBand(60, 50, -40), Oranges = new HslBand(0, 40, 0) };
        var c = new SKColor((byte)r, (byte)g, (byte)b);
        var result = Render(c, s);
        Assert.InRange(Math.Abs(result.Red - c.Red), 0, 1);
        Assert.InRange(Math.Abs(result.Green - c.Green), 0, 1);
        Assert.InRange(Math.Abs(result.Blue - c.Blue), 0, 1);
    }

    [Fact]
    public void AStrongSaturationBoost_KeepsTheHueOfARed()
    {
        // 14 degrees of drift towards orange when the out-of-gamut colour was pulled towards its luminance in RGB.
        var c = new SKColor(200, 40, 50);
        var result = Render(c, new AdjustmentSettings { Reds = new HslBand(0, 100, 0) });
        float hue0 = HslMath.Oklch(ColorMath.SrgbToLinear(200 / 255f), ColorMath.SrgbToLinear(40 / 255f), ColorMath.SrgbToLinear(50 / 255f)).Hue;
        float hue1 = HslMath.Oklch(ColorMath.SrgbToLinear(result.Red / 255f), ColorMath.SrgbToLinear(result.Green / 255f), ColorMath.SrgbToLinear(result.Blue / 255f)).Hue;
        Assert.InRange(Math.Abs(hue1 - hue0), 0f, 3f);
        Assert.True(result.Red >= c.Red);
    }
    private static SKColor Render(SKColor c, AdjustmentSettings s)
    {
        using var src = TestImages.Solid(c);
        using var dst = CpuAdjustmentRenderer.Render(src, s);
        return dst.GetPixel(0, 0);
    }

    [Fact]
    public void BlueLuminanceMinus_DarkensBlueButNotRedOrGrey()
    {
        var s = new AdjustmentSettings { Blues = new HslBand(0, 0, -100) };
        var blue = new SKColor(40, 80, 220);
        Assert.True(Render(blue, s).Blue < 180);
        Assert.Equal(new SKColor(220, 40, 40), Render(new SKColor(220, 40, 40), s));
        Assert.Equal(new SKColor(128, 128, 128), Render(new SKColor(128, 128, 128), s));
    }

    [Fact]
    public void RedHueShift_MovesTowardOrange()
    {
        var c = Render(new SKColor(220, 30, 30), new AdjustmentSettings { Reds = new HslBand(100, 0, 0) });
        Assert.True(c.Green > 60 && c.Blue < 40, c.ToString());
    }

    [Fact]
    public void GreenSaturationMinus100_MakesGreensGrey()
    {
        var c = Render(new SKColor(40, 200, 40), new AdjustmentSettings { Greens = new HslBand(0, -100, 0) });
        Assert.InRange(c.Red - c.Blue, -1, 1);
        Assert.InRange(c.Green - c.Red, -1, 1);
    }
}
