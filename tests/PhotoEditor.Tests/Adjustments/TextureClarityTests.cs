using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using PhotoEditor.Tests.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public sealed class TextureClarityTests
{
    /// <summary>
    /// 640 × 240: left half a medium-size texture (period ≈ 6 px, like skin or bark at this size), right half
    /// pixel-level noise (a ±6 checkerboard) on the same mid grey.
    /// </summary>
    private static SKBitmap TextureAndNoise()
    {
        var bmp = new SKBitmap(new SKImageInfo(640, 240, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 640; x++)
            {
                double v = x < 320
                    ? 128 + 12 * Math.Sin(x * 1.05) * Math.Sin(y * 1.05)
                    : 128 + ((x + y) % 2 == 0 ? 6 : -6);
                byte b = (byte)Math.Round(v);
                bmp.SetPixel(x, y, new SKColor(b, b, b));
            }
        return bmp;
    }

    private static double Std(SKBitmap b, int x0, int x1, int y0, int y1)
    {
        var v = new List<double>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                v.Add(b.GetPixel(x, y).Green);
        double mean = v.Average();
        return Math.Sqrt(v.Average(d => (d - mean) * (d - mean)));
    }

    [Fact]
    public void Texture_StrengthensMediumDetail_NotPixelNoise()
    {
        using var src = TextureAndNoise();
        using var more = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Texture = 100 });
        using var less = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Texture = -100 });
        double texture = Std(src, 20, 300, 20, 220), noise = Std(src, 340, 620, 20, 220);
        double textureUp = Std(more, 20, 300, 20, 220) / texture, noiseUp = Std(more, 340, 620, 20, 220) / noise;
        Assert.True(textureUp > 1.4, $"texture ×{textureUp:F2}");
        Assert.True(noiseUp < 1.15, $"noise ×{noiseUp:F2}");
        double textureDown = Std(less, 20, 300, 20, 220) / texture;
        Assert.True(textureDown < 0.7, $"texture ×{textureDown:F2} at −100");
    }

    /// <summary>
    /// 640 × 480: left, mid grey with large soft blotches (period ≈ 60 px, like clouds or a face's shape); right of
    /// x = 320 a dark flat area (a hard edge).
    /// </summary>
    private static SKBitmap BlotchesAndEdge()
    {
        var bmp = new SKBitmap(new SKImageInfo(640, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
            {
                double v = x < 320 ? 120 + 22 * Math.Sin(x * 0.105) * Math.Sin(y * 0.105) : 35;
                byte b = (byte)Math.Round(v);
                bmp.SetPixel(x, y, new SKColor(b, b, b));
            }
        return bmp;
    }

    [Fact]
    public void Clarity_AddsLocalContrast_WithoutAHaloAtAHardEdge()
    {
        using var src = BlotchesAndEdge();
        using var more = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Clarity = 100 });
        using var less = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Clarity = -100 });
        double before = Std(src, 20, 280, 20, 460);
        double up = Std(more, 20, 280, 20, 460) / before, down = Std(less, 20, 280, 20, 460) / before;
        Assert.True(up > 1.2, $"local contrast ×{up:F2}");
        Assert.True(down < 0.85, $"local contrast ×{down:F2} at −100");
        // The flat dark side stays as it was, right up to the edge (no glow / dark rim).
        for (int x = 321; x < 340; x++)
            Assert.InRange(more.GetPixel(x, 240).Green, 33, 38);
    }

    private static Mask MaskOf(AdjustmentSettings adjustments, params MaskComponent[] components) =>
        new() { Adjustments = adjustments, Components = [.. components] };

    public static TheoryData<string, EditState> ParityCases => new()
    {
        { "texture", new EditState { Adjustments = new AdjustmentSettings { Texture = 150, Exposure = 0.3 } } },
        { "texture negative + soften", new EditState { Adjustments = new AdjustmentSettings { Texture = -80, Soften = 40 } } },
        { "clarity", new EditState { Adjustments = new AdjustmentSettings { Clarity = 200, Highlights = -50 } } },
        { "clarity negative", new EditState { Adjustments = new AdjustmentSettings { Clarity = -100 } } },
        { "in a mask", new EditState
            {
                Adjustments = new AdjustmentSettings { Texture = 40 },
                Masks = [MaskOf(new AdjustmentSettings { Clarity = 80, Texture = -60, Soften = 30 }, new RectComponent(0.2f, 0.1f, 0.8f, 0.9f))],
            } },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void ShaderMatchesCpu(string name, EditState state)
    {
        using var src = TestImages.Varied();
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        int diff = TestImages.MaxDifference(cpu, gpu, out var at);
        Assert.True(diff <= 2, $"{name}: max channel difference {diff} at {at}");
    }

    [Fact]
    public void ShaderMatchesCpu_OnTheTestScenes()
    {
        foreach (var (scene, settings) in new[]
        {
            (TextureAndNoise(), new AdjustmentSettings { Texture = 100 }),
            (BlotchesAndEdge(), new AdjustmentSettings { Clarity = 100 }),
        })
        {
            using var src = scene;
            using var cpu = CpuAdjustmentRenderer.Render(src, settings);
            using var gpu = AdjustmentShader.RenderRaster(src, settings);
            Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
        }
    }
}
