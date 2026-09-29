using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public class ColorAdjustmentTests
{
    private static SKColor RenderPixel(SKColor color, AdjustmentSettings settings)
    {
        using var src = TestImages.Solid(color);
        using var dst = CpuAdjustmentRenderer.Render(src, settings);
        return dst.GetPixel(0, 0);
    }

    [Fact]
    public void WhiteBalanceGains_KeepGreyLuminance()
    {
        var (r, g, b) = PreparedAdjustments.WhiteBalanceGains(63, -41);
        Assert.Equal(1f, ToneCurve.Luminance(r, g, b), 5);
    }

    [Fact]
    public void Warmer_AddsRedRemovesBlue()
    {
        var c = RenderPixel(new SKColor(128, 128, 128), new AdjustmentSettings { Temperature = 50 });
        Assert.True(c.Red > 128 && c.Blue < 128, c.ToString());
    }

    [Fact]
    public void PositiveTint_IsMagenta()
    {
        var c = RenderPixel(new SKColor(128, 128, 128), new AdjustmentSettings { Tint = 50 });
        Assert.True(c.Green < c.Red && c.Green < c.Blue, c.ToString());
    }

    [Fact]
    public void SaturationMinus100_IsGrey()
    {
        var c = RenderPixel(new SKColor(200, 60, 30), new AdjustmentSettings { Saturation = -100 });
        Assert.InRange(Math.Abs(c.Red - c.Green), 0, 1);
        Assert.InRange(Math.Abs(c.Green - c.Blue), 0, 1);
    }

    [Fact]
    public void Saturation_DoesNotChangeGrey()
    {
        var c = RenderPixel(new SKColor(90, 90, 90), new AdjustmentSettings { Saturation = 100, Vibrance = 100 });
        Assert.Equal(new SKColor(90, 90, 90), c);
    }

    [Fact]
    public void Vibrance_BoostsMutedColoursMoreThanSaturatedOnes()
    {
        var settings = new AdjustmentSettings { Vibrance = 100 };
        var muted = new SKColor(140, 120, 110);
        var vivid = new SKColor(230, 40, 20);
        int mutedGain = Spread(RenderPixel(muted, settings)) - Spread(muted);
        int vividGain = Spread(RenderPixel(vivid, settings)) - Spread(vivid);
        Assert.True(mutedGain > 0);
        Assert.True(mutedGain * 1.0 / Spread(muted) > vividGain * 1.0 / Spread(vivid));

        static int Spread(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
    }
}
