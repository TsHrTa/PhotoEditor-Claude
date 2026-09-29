using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public class CpuAdjustmentRendererTests
{
    [Fact]
    public void DefaultSettings_LeaveImageUnchanged()
    {
        using var src = TestImages.Varied(withAlpha: false);
        using var dst = CpuAdjustmentRenderer.Render(src, AdjustmentSettings.Default);
        Assert.True(TestImages.MaxDifference(src, dst, out _) <= 1);
    }

    [Fact]
    public void DefaultSettings_KeepAlpha()
    {
        using var src = TestImages.Varied();
        using var dst = CpuAdjustmentRenderer.Render(src, AdjustmentSettings.Default);
        Assert.True(TestImages.MaxDifference(src, dst, out _) <= 2);
    }

    [Fact]
    public void ExposurePlusOne_DoublesLinearLight()
    {
        using var src = TestImages.Solid(new SKColor(100, 50, 20));
        using var dst = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Exposure = 1 });
        var c = dst.GetPixel(0, 0);
        Assert.Equal(Expected(100), c.Red, 1f);
        Assert.Equal(Expected(50), c.Green, 1f);
        Assert.Equal(Expected(20), c.Blue, 1f);

        static float Expected(byte v) => MathF.Round(255 * ColorMath.LinearToSrgb(Math.Min(1, 2 * ColorMath.SrgbToLinear(v / 255f))));
    }

    [Fact]
    public void Exposure_ClipsAtWhite()
    {
        using var src = TestImages.Solid(new SKColor(200, 200, 200));
        using var dst = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Exposure = 5 });
        Assert.Equal(SKColors.White, dst.GetPixel(0, 0));
    }

    [Fact]
    public void Parameter_SetClampsToRange()
    {
        var s = AdjustmentParameters.Exposure.Set(AdjustmentSettings.Default, 99);
        Assert.Equal(5, s.Exposure);
        Assert.Equal(5, AdjustmentParameters.Exposure.Get(s));
    }

    [Fact]
    public void Parameter_SetRoundsToStep()
    {
        Assert.Equal(1.23, AdjustmentParameters.Exposure.Set(AdjustmentSettings.Default, 1.2345).Exposure, 9);
        Assert.Equal(42, AdjustmentParameters.Contrast.Set(AdjustmentSettings.Default, 41.7).Contrast);
    }
}
