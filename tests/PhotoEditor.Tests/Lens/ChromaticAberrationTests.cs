using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.Tests.Lens;

public sealed class ChromaticAberrationTests
{
    /// <summary>
    /// 2400 × 1600 scene of dark and light patches (like branches against the sky), whose red image is scaled by
    /// <paramref name="red"/> and blue by <paramref name="blue"/> around the centre.
    /// </summary>
    private static SKBitmap Scene(double red, double blue)
    {
        const int w = 2400, h = 1600;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        static byte Value(double x, double y)
        {
            // Irregular cells ~40 px: smooth-edged blobs, so the edges have every direction.
            double v = Math.Sin(x * 0.11) * Math.Sin(y * 0.13) + Math.Sin(x * 0.047 + y * 0.061) + 0.6 * Math.Sin(x * 0.19 - y * 0.07);
            return v > 0.2 ? (byte)225 : (byte)30;
        }
        var pixels = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - w / 2.0, dy = y + 0.5 - h / 2.0;
                int i = (y * w + x) * 4;
                pixels[i] = Value(w / 2.0 + dx / red, h / 2.0 + dy / red);
                pixels[i + 1] = Value(w / 2.0 + dx, h / 2.0 + dy);
                pixels[i + 2] = Value(w / 2.0 + dx / blue, h / 2.0 + dy / blue);
                pixels[i + 3] = 255;
            }
        });
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return bmp;
    }

    [Fact]
    public void Measure_FindsTheRedAndBlueScales()
    {
        using var photo = Scene(1.003, 0.998);
        var (red, blue) = ChromaticAberration.Measure(photo);
        Assert.InRange(red, 1.0026, 1.0034);
        Assert.InRange(blue, 0.9976, 0.9984);
        using var clean = Scene(1, 1);
        Assert.Equal((1.0, 1.0), ChromaticAberration.Measure(clean));
    }

    [Fact]
    public void RemoveChromaticAberration_WithoutAProfile_MeasuresAndCorrects()
    {
        using var photo = Scene(1.004, 0.997);
        var state = new EditState { Adjustments = new AdjustmentSettings { RemoveChromaticAberration = true } };
        using var corrected = CpuAdjustmentRenderer.Render(photo, state, PhotoLens.Unknown);
        long Fringe(SKBitmap b)
        {
            long sum = 0;
            for (int y = 100; y < 1500; y += 7)
                for (int x = 100; x < 2300; x += 7)
                {
                    var c = b.GetPixel(x, y);
                    sum += Math.Abs(c.Red - c.Green) + Math.Abs(c.Blue - c.Green);
                }
            return sum;
        }
        Assert.True(Fringe(corrected) * 3 < Fringe(photo), $"fringe {Fringe(photo)} → {Fringe(corrected)}");
    }
}
