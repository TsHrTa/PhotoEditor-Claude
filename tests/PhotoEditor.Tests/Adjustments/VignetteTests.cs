using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public class VignetteTests
{
    [Fact]
    public void Distance_IsZeroAtCentreAndOneAtCorners()
    {
        foreach (float roundness in new[] { -1f, -0.5f, 0f, 0.5f, 1f })
        {
            Assert.Equal(0f, VignetteMath.Distance(0, 0, 300, 200, roundness), 4);
            Assert.Equal(1f, VignetteMath.Distance(1, 1, 300, 200, roundness), 3);
            Assert.Equal(1f, VignetteMath.Distance(-1, 1, 300, 200, roundness), 3);
        }
    }

    [Fact]
    public void Circle_HasEqualDistanceAtEqualPixelRadius()
    {
        // 300×200 image: 60 px right of centre is u = 0.4, 60 px down is v = 0.6.
        float right = VignetteMath.Distance(0.4f, 0, 300, 200, 1f);
        float down = VignetteMath.Distance(0, 0.6f, 300, 200, 1f);
        Assert.Equal(right, down, 4);
    }

    [Fact]
    public void Rectangular_IsFlatterAlongEdges()
    {
        // Mid-edge point is "further out" for a rectangular vignette than for the default ellipse.
        Assert.True(VignetteMath.Distance(1, 0, 300, 200, -1f) > VignetteMath.Distance(1, 0, 300, 200, 0f));
    }

    [Fact]
    public void NegativeAmount_DarkensCornersNotCentre()
    {
        using var src = TestImages.Solid(new SKColor(128, 128, 128), 41);
        using var dst = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { VignetteAmount = -100 });
        Assert.Equal(new SKColor(128, 128, 128), dst.GetPixel(20, 20));
        Assert.True(dst.GetPixel(0, 0).Red < 80);
    }

    [Fact]
    public void Defaults_AreNeutral()
    {
        Assert.False(PreparedAdjustments.From(AdjustmentSettings.Default).HasVignette);
        Assert.Equal(50, AdjustmentSettings.Default.VignetteMidpoint);
        Assert.Equal(50, AdjustmentParameters.VignetteFeather.DefaultValue);
    }
}
