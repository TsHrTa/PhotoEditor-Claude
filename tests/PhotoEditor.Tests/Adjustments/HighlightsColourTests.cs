using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public sealed class HighlightsColourTests
{
    private static float Saturation(SKColor c)
    {
        int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
        return max == 0 ? 0 : (max - min) / (float)max;
    }

    [Theory]
    [InlineData(-40)]
    [InlineData(-100)]
    public void LoweringHighlights_DarkensTheSky_WithoutGreyingIt(int amount)
    {
        var sky = new SKColor(120, 170, 235);
        using var src = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul));
        src.Erase(sky);
        using var result = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Highlights = amount });
        var c = result.GetPixel(8, 8);
        Assert.True(c.Green < sky.Green, $"not darker: {c}");
        Assert.True(Saturation(c) >= Saturation(sky) * 0.98f, $"greyer: {Saturation(sky):F2} -> {Saturation(c):F2} ({c})");
    }
}
