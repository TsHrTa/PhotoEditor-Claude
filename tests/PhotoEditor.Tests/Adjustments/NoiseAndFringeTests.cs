using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public sealed class NoiseAndFringeTests
{
    private static double Std(SKBitmap b, int x0, int x1, Func<SKColor, double> channel)
    {
        var v = new List<double>();
        for (int y = 4; y < b.Height - 4; y++)
            for (int x = x0; x < x1; x++)
                v.Add(channel(b.GetPixel(x, y)));
        double mean = v.Average();
        return Math.Sqrt(v.Average(d => (d - mean) * (d - mean)));
    }

    private static double Chroma(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));

    [Fact]
    public void LuminanceNoise_IsSmoothed_EdgeKept()
    {
        // Left: grey 90 with ±8 grey noise; right: white.
        var rnd = new Random(1);
        using var src = new SKBitmap(new SKImageInfo(80, 40, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 80; x++)
            {
                byte v = x < 40 ? (byte)(90 + rnd.Next(-8, 9)) : (byte)245;
                src.SetPixel(x, y, new SKColor(v, v, v));
            }
        using var out1 = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { NoiseLuminance = 100 });
        Assert.True(Std(out1, 4, 36, c => c.Red) < Std(src, 4, 36, c => c.Red) / 2, "noise not reduced");
        Assert.InRange(out1.GetPixel(39, 20).Red, 70, 110);
        Assert.InRange(out1.GetPixel(40, 20).Red, 235, 255);
    }

    [Fact]
    public void ColorNoise_IsSmoothed_BrightnessDetailKept()
    {
        // Grey with random colour speckles of equal brightness, plus a 1-px brightness stripe every 6 px.
        var rnd = new Random(2);
        using var src = new SKBitmap(new SKImageInfo(60, 40, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 60; x++)
            {
                int d = rnd.Next(-14, 15);
                int baseV = x % 6 == 0 ? 170 : 120;
                src.SetPixel(x, y, new SKColor((byte)(baseV + d), (byte)(baseV - d / 3), (byte)(baseV - d)));
            }
        using var result = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { NoiseColor = 100 });
        Assert.True(Std(result, 4, 56, Chroma) < Std(src, 4, 56, Chroma) / 2, "colour noise not reduced");
        // The bright stripes stay.
        Assert.True(result.GetPixel(30, 20).Green > result.GetPixel(32, 20).Green + 30);
    }

    [Fact]
    public void Defringe_RemovesPurpleNextToEdges_KeepsPurpleAreas()
    {
        // Left: white; a 2-px purple fringe; then dark. Far right: a flat purple area (no edge nearby).
        using var src = new SKBitmap(new SKImageInfo(80, 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 80; x++)
            {
                var c = x < 20 ? new SKColor(250, 250, 250)
                    : x < 22 ? new SKColor(150, 60, 190)
                    : x < 55 ? new SKColor(30, 30, 30)
                    : new SKColor(150, 60, 190);
                src.SetPixel(x, y, c);
            }
        using var result = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { DefringePurple = 100 });
        Assert.True(Chroma(result.GetPixel(20, 10)) < Chroma(src.GetPixel(20, 10)) / 3, $"fringe {result.GetPixel(20, 10)}");
        Assert.True(Chroma(result.GetPixel(70, 10)) > Chroma(src.GetPixel(70, 10)) * 0.9, "flat purple area must stay");
        using var green = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { DefringeGreen = 100 });
        Assert.True(Chroma(green.GetPixel(20, 10)) > Chroma(src.GetPixel(20, 10)) * 0.9, "green defringe must leave purple alone");
    }
}
