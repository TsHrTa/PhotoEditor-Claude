using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public sealed class LocalToneTests
{
    private const int Horizon = 240;

    /// <summary>
    /// 640 × 480: top, a bright sky with fine cloud texture (±18 levels, period ≈ 5 px — much finer than the map's
    /// smoothing, as in a real 24 MP photo); bottom, dark ground. Horizon at y = 240.
    /// </summary>
    private static SKBitmap SkyAndGround()
    {
        var bmp = new SKBitmap(new SKImageInfo(640, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
            {
                byte v = y < Horizon
                    ? (byte)(215 + 18 * Math.Sin(x * 1.3) * Math.Cos(y * 1.1))
                    : (byte)(40 + 6 * Math.Sin(x * 0.8));
                bmp.SetPixel(x, y, new SKColor(v, v, v));
            }
        return bmp;
    }

    private static (double Mean, double Std) Stats(SKBitmap b, int y0, int y1)
    {
        var v = new List<double>();
        for (int y = y0; y < y1; y++)
            for (int x = 10; x < b.Width - 10; x++)
                v.Add(b.GetPixel(x, y).Red);
        double mean = v.Average();
        return (mean, Math.Sqrt(v.Average(d => (d - mean) * (d - mean))));
    }

    /// <summary>The same settings as a global per-pixel curve (the previous behaviour), for comparison.</summary>
    private static SKBitmap Global(SKBitmap src, AdjustmentSettings s)
    {
        var p = PreparedAdjustments.From(s);
        var result = new SKBitmap(src.Info);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                float r = ColorMath.SrgbToLinear(c.Red / 255f), g = ColorMath.SrgbToLinear(c.Green / 255f), b = ColorMath.SrgbToLinear(c.Blue / 255f);
                CpuAdjustmentRenderer.ApplyLinear(ref r, ref g, ref b, p);
                byte B(float v) => (byte)Math.Round(ColorMath.LinearToSrgb(Math.Clamp(v, 0f, 1f)) * 255);
                result.SetPixel(x, y, new SKColor(B(r), B(g), B(b)));
            }
        return result;
    }

    [Fact]
    public void Highlights_DarkenTheSky_KeepCloudTexture()
    {
        using var src = SkyAndGround();
        var settings = new AdjustmentSettings { Highlights = -100 };
        using var local = CpuAdjustmentRenderer.Render(src, settings);
        using var global = Global(src, settings);
        var before = Stats(src, 20, 180);
        var after = Stats(local, 20, 180);
        var flat = Stats(global, 20, 180);
        Assert.True(after.Mean < before.Mean - 15, $"sky mean {before.Mean:0} → {after.Mean:0}");
        string numbers = $"texture {before.Std:0.0} → {after.Std:0.0} (mean {before.Mean:0} → {after.Mean:0}; global: std {flat.Std:0.0}, mean {flat.Mean:0})";
        // Texture relative to brightness is kept (a darker sky shows the same clouds, just darker) ...
        Assert.True(after.Std / after.Mean > 0.9 * before.Std / before.Mean, numbers);
        // ... and clearly more than with the old global curve at the same brightness.
        Assert.True(after.Std > flat.Std * 1.2, numbers);
    }

    [Fact]
    public void Shadows_LiftTheGround_WithoutAHaloAtTheHorizon()
    {
        using var src = SkyAndGround();
        var settings = new AdjustmentSettings { Shadows = 100 };
        using var lifted = CpuAdjustmentRenderer.Render(src, settings);
        using var global = Global(src, settings);
        Assert.True(Stats(lifted, 300, 460).Mean > Stats(src, 300, 460).Mean + 20, "ground not lifted");
        // Sky pixels just above the horizon change as a global curve would change them: no glow from the lifted
        // ground next to them (a halo), and ground pixels just below get no dark rim from the bright sky.
        for (int x = 20; x < 620; x += 25)
        {
            Assert.InRange(lifted.GetPixel(x, Horizon - 3).Red - global.GetPixel(x, Horizon - 3).Red, -5, 5);
            Assert.InRange(lifted.GetPixel(x, Horizon + 3).Red - global.GetPixel(x, Horizon + 3).Red, -8, 8);
        }
    }

    [Fact]
    public void BaseOfAFlatArea_IsThePixelItself()
    {
        using var flat = new SKBitmap(new SKImageInfo(100, 80, SKColorType.Rgba8888, SKAlphaType.Premul));
        flat.Erase(new SKColor(120, 120, 120));
        var map = ToneBaseMap.Compute(flat);
        float l = ToneBaseMap.LogLuminance(120, 120, 120, 255);
        Assert.Equal(1f, map.BaseRatio(50, 40, 100, 80, l), 2);
    }
}
