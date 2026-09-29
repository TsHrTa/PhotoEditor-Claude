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
        var p = PreparedAdjustments.From(new AdjustmentSettings { Greens = new HslBand(0, 100, 0) });
        Assert.Equal(2f, HslMath.Interpolate(120, p.Hsl).SatFactor, 4);
        Assert.Equal(1.5f, HslMath.Interpolate(150, p.Hsl).SatFactor, 4); // halfway to aquas
        Assert.Equal(1f, HslMath.Interpolate(200, p.Hsl).SatFactor, 4);
    }

    [Fact]
    public void Interpolate_WrapsAroundBetweenMagentasAndReds()
    {
        var p = PreparedAdjustments.From(new AdjustmentSettings { Reds = new HslBand(100, 0, 0) });
        Assert.Equal(HslBands.MaxHueShift, HslMath.Interpolate(0, p.Hsl).HueShift, 4);
        Assert.Equal(HslBands.MaxHueShift / 2, HslMath.Interpolate(330, p.Hsl).HueShift, 4);
        Assert.Equal(0f, HslMath.Interpolate(300, p.Hsl).HueShift, 4);
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
