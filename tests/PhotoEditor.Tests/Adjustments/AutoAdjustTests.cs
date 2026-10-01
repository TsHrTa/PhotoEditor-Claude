using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public class AutoAdjustTests
{
    /// <summary>A scene with a smooth brightness range (scaled by <paramref name="gain"/>) and a colour tint.</summary>
    private static SKBitmap Scene(float gain, float r = 1, float g = 1, float b = 1, float contrast = 1)
    {
        var bmp = new SKBitmap(new SKImageInfo(200, 120, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            // Perceptual ramp 0.05..0.95 with some texture, compressed around 0.5 by `contrast`.
            float t = 0.05f + 0.9f * x / (bmp.Width - 1) + 0.03f * MathF.Sin(y * 0.7f);
            t = 0.5f + (t - 0.5f) * contrast;
            float lin = MathF.Pow(Math.Clamp(t, 0f, 1f), 2.2f) * gain;
            byte C(float k) => (byte)Math.Clamp((int)MathF.Round(ColorMath.LinearToSrgb(Math.Min(1f, lin * k)) * 255f), 0, 255);
            bmp.SetPixel(x, y, new SKColor(C(r), C(g), C(b)));
        }
        return bmp;
    }

    private static float MedianAfter(SKBitmap image, AdjustmentSettings s)
    {
        var v = AutoAdjust.Perceptual(AutoAdjust.Sample(image, Crop.None), s);
        return v[v.Length / 2];
    }

    [Fact]
    public void DarkPhoto_IsBrightened()
    {
        using var img = Scene(0.1f);
        var s = AutoAdjust.Suggest(img, Crop.None, AdjustmentSettings.Default);
        Assert.True(s.Exposure > 1, $"exposure {s.Exposure}");
        Assert.True(MedianAfter(img, s) > MedianAfter(img, AdjustmentSettings.Default) + 0.15f);
    }

    [Fact]
    public void BrightPhoto_IsDarkened()
    {
        using var img = Scene(4f);
        var s = AutoAdjust.Suggest(img, Crop.None, AdjustmentSettings.Default);
        Assert.True(s.Exposure < -0.5, $"exposure {s.Exposure}");
        Assert.True(MedianAfter(img, s) < MedianAfter(img, AdjustmentSettings.Default) - 0.15f);
    }

    [Fact]
    public void WellExposedPhoto_ChangesLittle()
    {
        using var img = Scene(1f);
        var s = AutoAdjust.Suggest(img, Crop.None, AdjustmentSettings.Default);
        Assert.InRange(s.Exposure, -0.4, 0.4);
        Assert.InRange(s.Temperature, -3, 3);
        Assert.InRange(s.Tint, -3, 3);
    }

    [Fact]
    public void BlueCast_IsWarmed_GreenCast_GetsMagenta()
    {
        using var blue = Scene(1f, r: 0.7f, b: 1.3f);
        Assert.True(AutoAdjust.Suggest(blue, Crop.None, AdjustmentSettings.Default).Temperature > 10);
        using var green = Scene(1f, g: 1.3f);
        Assert.True(AutoAdjust.Suggest(green, Crop.None, AdjustmentSettings.Default).Tint > 10);
    }

    [Fact]
    public void FlatPhoto_GetsMoreRange()
    {
        using var img = Scene(1f, contrast: 0.4f);
        var s = AutoAdjust.Suggest(img, Crop.None, AdjustmentSettings.Default);
        var before = AutoAdjust.Perceptual(AutoAdjust.Sample(img, Crop.None), AdjustmentSettings.Default);
        var after = AutoAdjust.Perceptual(AutoAdjust.Sample(img, Crop.None), s);
        float Range(float[] v) => v[(int)(v.Length * 0.99)] - v[(int)(v.Length * 0.01)];
        Assert.True(Range(after) > Range(before) + 0.12f, $"range {Range(before)} → {Range(after)}");
    }

    [Fact]
    public void KeepsHslVignetteAndSaturation_IsDeterministic()
    {
        using var img = Scene(0.3f);
        var current = new AdjustmentSettings { Saturation = 12, Blues = new HslBand(10, 0, 0), VignetteAmount = -20, Exposure = 3 };
        var a = AutoAdjust.Suggest(img, Crop.None, current);
        Assert.Equal(12, a.Saturation);
        Assert.Equal(current.Blues, a.Blues);
        Assert.Equal(-20, a.VignetteAmount);
        // Ignores the current light settings: same result from a different starting point.
        Assert.Equal(a, AutoAdjust.Suggest(img, Crop.None, current with { Exposure = -2, Contrast = 50 }));
    }

    [Fact]
    public void OnlyTheCroppedAreaCounts()
    {
        using var img = Scene(1f); // dark on the left, bright on the right
        var left = AutoAdjust.Suggest(img, new Crop { Right = 0.3 }, AdjustmentSettings.Default);
        var right = AutoAdjust.Suggest(img, new Crop { Left = 0.7 }, AdjustmentSettings.Default);
        Assert.True(left.Exposure > right.Exposure + 1);
    }

    [Fact]
    public void SoftLimit_IsLinearNearZeroAndCompressesLargeValues()
    {
        Assert.Equal(0.1, AutoAdjust.SoftLimit(0.1, 1.5), 1);
        Assert.InRange(AutoAdjust.SoftLimit(4, 1.5), 1.8, 2.1);
        Assert.Equal(-AutoAdjust.SoftLimit(3, 1.5), AutoAdjust.SoftLimit(-3, 1.5));
    }

    [Fact]
    public void RoundedNegativeZero_IsPositiveZero()
    {
        var s = AdjustmentParameters.Highlights.Set(AdjustmentSettings.Default, -0.2);
        Assert.False(double.IsNegative(s.Highlights));
    }
}
