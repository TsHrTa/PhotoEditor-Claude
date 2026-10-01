using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Export;

public sealed class ExportResamplingTests
{
    private static SKBitmap Half(int w, int h, Func<int, int, float> value)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul));
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bitmap.GetPixelSpan());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                halves[i] = halves[i + 1] = halves[i + 2] = (Half)value(x, y);
                halves[i + 3] = (Half)1f;
            }
        return bitmap;
    }

    private static float Red(SKBitmap f16, int x, int y) =>
        (float)System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(f16.GetPixelSpan())[(y * f16.Width + x) * 4];

    [Fact]
    public void An_unrotated_crop_copies_whole_pixels_exactly()
    {
        using var source = TestImages.Varied(64, 64, withAlpha: false);
        // Left 0.2 → 12.8 px: a fractional offset that a sampler would use to blur the picture by 0.2 px.
        var crop = new Crop { Left = 0.2, Top = 0.1, Right = 0.7, Bottom = 0.8 };
        using var cropped = CpuAdjustmentRenderer.ApplyCrop(source, crop);
        var (w, h) = crop.OutputSize(64, 64);
        Assert.Equal((w, h), (cropped.Width, cropped.Height));
        var f = crop.Frame(64, 64);
        int x0 = (int)Math.Round(f.CenterX - w / 2.0), y0 = (int)Math.Round(f.CenterY - h / 2.0);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(source.GetPixel(x0 + x, y0 + y), cropped.GetPixel(x, y));
    }

    [Fact]
    public void A_straightened_crop_keeps_a_step_edge_sharp()
    {
        // A vertical step edge; a straightening of 0.01 degrees leaves it a clean step (Catmull-Rom), where
        // bilinear sampling would give a 2-pixel ramp at a half-pixel offset.
        using var source = Half(200, 100, (x, _) => x < 100 ? 0.2f : 0.8f);
        using var cropped = CpuAdjustmentRenderer.ApplyCrop(source, new Crop { Left = 0.1, Right = 0.9, Top = 0.1, Bottom = 0.9, Angle = 0.01 });
        int left = 0, right = 0;
        for (int x = 0; x < cropped.Width; x++)
        {
            float v = Red(cropped, x, 40);
            if (v < 0.3f) left = x;
            if (v > 0.7f && right == 0) right = x;
        }
        Assert.InRange(right - left, 1, 2);
    }

    [Fact]
    public void An_export_resize_of_a_float_render_averages_in_linear_light()
    {
        // 1-pixel black and white stripes: the light is half, which is encoded 0.735 (not 0.5).
        using var source = Half(64, 64, (x, _) => x % 2 == 0 ? 1f : 0f);
        using var small = ImageExporter.Resize(source, 16);
        Assert.Equal(SKColorType.RgbaF16, small.ColorType);
        Assert.InRange(Red(small, 8, 8), 0.72f, 0.75f);
    }

    [Fact]
    public void The_float_resampler_keeps_a_flat_colour_and_values_above_white()
    {
        using var flat = Half(40, 40, (_, _) => 0.4f);
        using var a = FloatResampler.TryResize(flat, 10, 10)!;
        Assert.Equal(0.4f, Red(a, 5, 5), 3);
        using var bright = Half(40, 40, (_, _) => 1.6f);
        using var b = FloatResampler.TryResize(bright, 10, 10)!;
        Assert.InRange(Red(b, 5, 5), 1.58f, 1.62f);
        using var bytes = TestImages.Solid(new SKColor(1, 2, 3), 32);
        Assert.Null(FloatResampler.TryResize(bytes, 8, 8));
    }
}
