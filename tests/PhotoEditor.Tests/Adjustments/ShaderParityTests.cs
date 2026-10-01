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
        { "vignette dark", new AdjustmentSettings { VignetteAmount = -80 } },
        { "vignette light round", new AdjustmentSettings { VignetteAmount = 60, VignetteRoundness = 100, VignetteMidpoint = 20, VignetteFeather = 90 } },
        { "vignette rect hard", new AdjustmentSettings { VignetteAmount = -100, VignetteRoundness = -70, VignetteMidpoint = 70, VignetteFeather = 0 } },
        { "sharpen", new AdjustmentSettings { SharpenAmount = 100 } },
        { "sharpen wide masked", new AdjustmentSettings { SharpenAmount = 150, SharpenRadius = 1.7, SharpenMasking = 60 } },
        { "sharpen + light", new AdjustmentSettings { SharpenAmount = 60, SharpenRadius = 2, Exposure = 0.5, Contrast = 30 } },
        { "all light", new AdjustmentSettings { Exposure = 0.4, Contrast = 25, Highlights = -40, Shadows = 35, Whites = 15, Blacks = -10 } },
        { "extreme light", new AdjustmentSettings { Exposure = -6, Contrast = 200, Highlights = 200, Shadows = -200, Whites = -200, Blacks = 200 } },
        // Contrast −200 is nearly vertical at black and white, so combined with the local highlights / shadows it
        // magnifies rounding differences; −100 keeps the case meaningful. (Contrast −200 alone: "contrast extremes".)
        { "extreme light 2", new AdjustmentSettings { Exposure = 3, Contrast = -100, Highlights = -200, Shadows = 200, Whites = 150, Blacks = -150 } },
        { "contrast extremes", new AdjustmentSettings { Exposure = 3, Contrast = -200, Whites = 150, Blacks = -150 } },
        { "extreme colour", new AdjustmentSettings { Temperature = 200, Tint = -200, Saturation = 200, Vibrance = 200 } },
        { "extreme hsl", new AdjustmentSettings { Reds = new HslBand(200, 200, -200), Blues = new HslBand(-200, -100, 200) } },
        { "extreme vignette + sharpen", new AdjustmentSettings { VignetteAmount = -200, SharpenAmount = 300 } },
        { "soften", new AdjustmentSettings { Soften = 100 } },
        { "noise luminance", new AdjustmentSettings { NoiseLuminance = 100 } },
        { "noise color", new AdjustmentSettings { NoiseColor = 100 } },
        { "defringe", new AdjustmentSettings { DefringePurple = 100, DefringeGreen = 100 } },
        { "defringe wide ranges", new AdjustmentSettings { DefringePurple = 80, DefringeGreen = 90,
            DefringePurpleHueLow = 0, DefringePurpleHueHigh = 100, DefringeGreenHueLow = 10, DefringeGreenHueHigh = 95 } },
        { "all detail", new AdjustmentSettings { NoiseLuminance = 60, NoiseColor = 80, DefringePurple = 50, SharpenAmount = 70, Soften = 30, Exposure = 0.4 } },
        { "curve parametric", new AdjustmentSettings { CurveHighlights = -60, CurveLights = 40, CurveDarks = -30, CurveShadows = 80 } },
        { "curve parametric splits", new AdjustmentSettings { CurveHighlights = 100, CurveShadows = -100, CurveShadowSplit = 40, CurveMidtoneSplit = 45, CurveHighlightSplit = 60 } },
        { "curve s-shape", new AdjustmentSettings { Curve = ToneCurvePanelTests.Curve((0, 0), (0.25, 0.18), (0.75, 0.85), (1, 1)) } },
        { "curve matte + channels", new AdjustmentSettings { Curve = ToneCurvePanelTests.Curve((0, 0.1), (1, 0.95)),
            CurveRed = ToneCurvePanelTests.Curve((0, 0), (0.5, 0.6), (1, 1)), CurveBlue = ToneCurvePanelTests.Curve((0, 0.08), (0.6, 0.5), (1, 1)) } },
        { "curve + light", new AdjustmentSettings { Exposure = 0.8, Contrast = 30, CurveLights = 50, Curve = ToneCurvePanelTests.Curve((0, 0), (0.4, 0.3), (1, 1)) } },
        { "soften + sharpen + light", new AdjustmentSettings { Soften = 60, SharpenAmount = 80, Exposure = 0.3, Contrast = 20 } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShaderMatchesCpu(string name, AdjustmentSettings settings)
    {
        using var src = TestImages.Varied();
        using var cpu = CpuAdjustmentRenderer.Render(src, settings);
        using var gpu = AdjustmentShader.RenderRaster(src, settings);
        int diff = TestImages.MaxDifference(cpu, gpu, out var at);
        // Sharpening can overshoot a saturated colour above white; the roll to white then magnifies tiny float
        // differences of the overshoot in the darker channels.
        Assert.True(diff <= (settings.SharpenAmount > 0 ? 3 : 2), $"{name}: max channel difference {diff} at {at}");
        if (!settings.IsDefault)
            Assert.True(TestImages.MaxDifference(src, gpu, out _) > 2, $"{name}: shader had no effect");
    }
}
