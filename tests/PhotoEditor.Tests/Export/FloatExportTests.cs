using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Export;

public sealed class FloatExportTests
{
    private static float Channel(SKBitmap f16, int x, int y, int c)
    {
        var halves = f16.GetPixelSpan();
        int at = (y * f16.Width + x) * 8 + c * 2;
        return (float)BitConverter.ToHalf(halves.Slice(at, 2));
    }

    [Fact]
    public void The_float_render_matches_the_8_bit_render_before_rounding()
    {
        using var source = TestImages.Varied(48, 48, withAlpha: false);
        var state = new EditState { Adjustments = AdjustmentSettings.Default with { Exposure = 0.4, Contrast = 20, Saturation = 15 } };
        using var bytes = CpuAdjustmentRenderer.Render(source, state);
        using var floats = CpuAdjustmentRenderer.RenderFloat(source, state);
        Assert.Equal(SKColorType.RgbaF16, floats.ColorType);
        for (int y = 0; y < 48; y += 3)
            for (int x = 0; x < 48; x += 3)
            {
                var px = bytes.GetPixel(x, y);
                Assert.InRange(Math.Abs(Channel(floats, x, y, 0) * 255f - px.Red), 0, 0.6);
                Assert.InRange(Math.Abs(Channel(floats, x, y, 1) * 255f - px.Green), 0, 0.6);
                Assert.InRange(Math.Abs(Channel(floats, x, y, 2) * 255f - px.Blue), 0, 0.6);
            }
    }

    [Fact]
    public void Rounding_without_dither_is_plain_and_with_dither_keeps_the_average_and_the_ends()
    {
        // A very slow ramp: 4000 pixels from 100.0 to 100.9 (in 8-bit units) — plain rounding gives two long bands.
        int w = 4000;
        using var f16 = new SKBitmap(new SKImageInfo(w, 8, SKColorType.RgbaF16, SKAlphaType.Premul));
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(f16.GetPixelSpan());
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < w; x++)
            {
                float v = (100f + 0.9f * x / (w - 1)) / 255f;
                int i = (y * w + x) * 4;
                span[i] = span[i + 1] = span[i + 2] = (Half)v;
                span[i + 3] = (Half)1f;
            }
        using var plain = FloatBitmap.ToBytes(f16, dither: false);
        using var dithered = FloatBitmap.ToBytes(f16, dither: true);
        int Transitions(SKBitmap b, int row)
        {
            int n = 0;
            for (int x = 1; x < w; x++)
                if (b.GetPixel(x, row).Red != b.GetPixel(x - 1, row).Red)
                    n++;
            return n;
        }
        Assert.True(Transitions(plain, 0) <= 2, "plain rounding: one step, two bands");
        Assert.True(Transitions(dithered, 0) > 100, "dither breaks the bands up");
        // The average of the dithered rows follows the true ramp (≈ 100.45 on average).
        double mean = 0;
        for (int x = 0; x < w; x++)
            mean += dithered.GetPixel(x, 0).Red;
        Assert.InRange(mean / w, 100.35, 100.55);
        // The same bytes every time.
        using var again = FloatBitmap.ToBytes(f16, dither: true);
        Assert.Equal(dithered.Bytes, again.Bytes);
    }

    [Fact]
    public void Flat_black_and_white_stay_exact_with_dither()
    {
        using var f16 = new SKBitmap(new SKImageInfo(64, 64, SKColorType.RgbaF16, SKAlphaType.Premul));
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(f16.GetPixelSpan());
        for (int i = 0; i < span.Length; i += 4)
        {
            bool white = (i / 4) % 64 >= 32;
            span[i] = span[i + 1] = span[i + 2] = (Half)(white ? 1f : 0f);
            span[i + 3] = (Half)1f;
        }
        using var bytes = FloatBitmap.ToBytes(f16);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                Assert.Equal(x >= 32 ? 255 : 0, bytes.GetPixel(x, y).Red);
    }

    [Fact]
    public void The_crop_of_a_float_render_stays_float()
    {
        using var source = TestImages.Varied(64, 64, withAlpha: false);
        var crop = new Crop { Left = 0.1, Top = 0.1, Right = 0.9, Bottom = 0.9, Angle = 3.0 };
        using var floats = CpuAdjustmentRenderer.RenderFloat(source, new EditState { Crop = crop });
        using var cropped = CpuAdjustmentRenderer.ApplyCrop(floats, crop);
        Assert.Equal(SKColorType.RgbaF16, cropped.ColorType);
    }
}
