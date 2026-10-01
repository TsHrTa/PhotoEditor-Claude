using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Tests.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public sealed class DehazeTests
{
    /// <summary>A scene with dark and coloured detail, then a grey-white haze veil that is thicker at the top.</summary>
    private static SKBitmap HazyScene()
    {
        var bmp = new SKBitmap(new SKImageInfo(120, 80, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 120; x++)
            {
                // Scene: checker of dark green and brick red.
                bool check = (x / 10 + y / 10) % 2 == 0;
                float r = check ? 0.05f : 0.45f, g = check ? 0.25f : 0.12f, b = check ? 0.05f : 0.08f;
                float veil = 0.7f - 0.5f * y / 79f; // haze amount
                float air = 0.85f;
                r = r * (1 - veil) + air * veil;
                g = g * (1 - veil) + air * veil;
                b = b * (1 - veil) + (air + 0.05f) * veil;
                bmp.SetPixel(x, y, new SKColor(ToByte(r), ToByte(g), ToByte(b)));
            }
        return bmp;
    }

    private static byte ToByte(float linear) => (byte)Math.Round(ColorMath.LinearToSrgb(Math.Clamp(linear, 0f, 1f)) * 255);

    private static double Contrast(SKBitmap b, int y0, int y1)
    {
        var v = new List<double>();
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < b.Width; x++)
                v.Add(b.GetPixel(x, y).Red);
        double mean = v.Average();
        return Math.Sqrt(v.Average(d => (d - mean) * (d - mean)));
    }

    [Fact]
    public void HazeMap_FindsTheThickHazeAndItsColour()
    {
        using var scene = HazyScene();
        var map = HazeMap.Compute(scene);
        float top = map.Sample(60, 5, 120, 80), bottom = map.Sample(60, 75, 120, 80);
        Assert.True(top < bottom - 0.2f, $"transmission top {top}, bottom {bottom}");
        // The haze colour is taken from the haziest bright pixels: a light grey here.
        foreach (var c in new[] { map.LightR, map.LightG, map.LightB })
            Assert.InRange(c, 0.5f, 0.95f);
    }

    [Fact]
    public void Dehaze_RestoresContrast_NegativeAddsHaze()
    {
        using var scene = HazyScene();
        using var clear = CpuAdjustmentRenderer.Render(scene, new AdjustmentSettings { Dehaze = 100 });
        using var hazier = CpuAdjustmentRenderer.Render(scene, new AdjustmentSettings { Dehaze = -100 });
        double before = Contrast(scene, 0, 25), after = Contrast(clear, 0, 25), veiled = Contrast(hazier, 0, 25);
        Assert.True(after > before * 1.8, $"contrast in the haze {before:0.0} → {after:0.0}");
        Assert.True(veiled < before * 0.7, $"contrast with added haze {before:0.0} → {veiled:0.0}");
    }

    public static TheoryData<string, EditState> Cases => new()
    {
        { "dehaze", new EditState { Adjustments = new AdjustmentSettings { Dehaze = 80 } } },
        { "add haze + light", new EditState { Adjustments = new AdjustmentSettings { Dehaze = -60, Exposure = 0.3, Contrast = 20 } } },
        { "dehaze in mask", new EditState
            {
                Masks = [new Mask { Components = [new RectComponent(0.1f, 0f, 0.8f, 0.6f)], Adjustments = new AdjustmentSettings { Dehaze = 100, Shadows = 20 } }],
            } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShaderMatchesCpu(string name, EditState state)
    {
        using var src = HazyScene();
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        int diff = TestImages.MaxDifference(cpu, gpu, out var at);
        Assert.True(diff <= 2, $"{name}: max channel difference {diff} at {at}");
    }
}
