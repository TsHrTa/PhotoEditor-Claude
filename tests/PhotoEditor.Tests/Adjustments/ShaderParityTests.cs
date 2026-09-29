using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

/// <summary>The SkSL shader (run on Skia's CPU backend) must match the C# CPU renderer.</summary>
public class ShaderParityTests
{
    public static TheoryData<string, AdjustmentSettings> Cases => new()
    {
        { "default", AdjustmentSettings.Default },
        { "exposure+", new AdjustmentSettings { Exposure = 1.3 } },
        { "exposure-", new AdjustmentSettings { Exposure = -2.1 } },
        { "contrast+", new AdjustmentSettings { Contrast = 80 } },
        { "contrast-", new AdjustmentSettings { Contrast = -60 } },
        { "highlights-", new AdjustmentSettings { Exposure = 1, Highlights = -100 } },
        { "highlights+", new AdjustmentSettings { Highlights = 70 } },
        { "shadows+", new AdjustmentSettings { Shadows = 100 } },
        { "shadows-", new AdjustmentSettings { Shadows = -100 } },
        { "whites", new AdjustmentSettings { Whites = 60 } },
        { "blacks", new AdjustmentSettings { Blacks = -80 } },
        { "warm", new AdjustmentSettings { Temperature = 70, Tint = 20 } },
        { "cool green", new AdjustmentSettings { Temperature = -100, Tint = -100 } },
        { "saturation+", new AdjustmentSettings { Saturation = 80 } },
        { "greyscale", new AdjustmentSettings { Saturation = -100 } },
        { "vibrance", new AdjustmentSettings { Vibrance = 100 } },
        { "vibrance-", new AdjustmentSettings { Vibrance = -70, Saturation = 20 } },
        { "hsl reds", new AdjustmentSettings { Reds = new HslBand(60, 50, -40) } },
        { "hsl blues", new AdjustmentSettings { Blues = new HslBand(-100, -100, -100), Aquas = new HslBand(30, 100, 100) } },
        { "hsl magentas", new AdjustmentSettings { Magentas = new HslBand(100, 100, 50), Purples = new HslBand(-50, 20, 0) } },
        { "hsl greens", new AdjustmentSettings { Greens = new HslBand(40, -60, 70), Yellows = new HslBand(-20, 30, -30) } },
        { "all light", new AdjustmentSettings { Exposure = 0.4, Contrast = 25, Highlights = -40, Shadows = 35, Whites = 15, Blacks = -10 } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShaderMatchesCpu(string name, AdjustmentSettings settings)
    {
        using var src = TestImages.Varied();
        using var cpu = CpuAdjustmentRenderer.Render(src, settings);
        using var gpu = AdjustmentShader.RenderRaster(src, settings);
        int diff = TestImages.MaxDifference(cpu, gpu, out var at);
        Assert.True(diff <= 2, $"{name}: max channel difference {diff} at {at}");
        if (!settings.IsDefault)
            Assert.True(TestImages.MaxDifference(src, gpu, out _) > 2, $"{name}: shader had no effect");
    }
}
