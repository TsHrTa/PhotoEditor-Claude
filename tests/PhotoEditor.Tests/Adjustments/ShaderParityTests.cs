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
