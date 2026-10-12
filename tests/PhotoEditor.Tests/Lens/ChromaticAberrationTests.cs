using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.Tests.Lens;

public sealed class ChromaticAberrationTests
{
    /// <summary>
    /// 2400 × 1600 scene of dark and light patches (like branches against the sky), whose red image is scaled by
    /// <paramref name="red"/> around the centre and moved by (<paramref name="redX"/>, <paramref name="redY"/>) pixels,
    /// blue likewise.
    /// </summary>
    private static SKBitmap Scene(double red, double blue, double redX = 0, double redY = 0, double blueX = 0, double blueY = 0)
    {
        const int w = 2400, h = 1600;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        static byte Value(double x, double y)
        {
            // Irregular cells ~40 px with soft (≈ 2 px) edges in every direction, as a lens draws them.
            double v = Math.Sin(x * 0.11) * Math.Sin(y * 0.13) + Math.Sin(x * 0.047 + y * 0.061) + 0.6 * Math.Sin(x * 0.19 - y * 0.07);
            return (byte)(30 + 195 / (1 + Math.Exp(-(v - 0.2) * 12)));
        }
        var pixels = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - w / 2.0, dy = y + 0.5 - h / 2.0;
                int i = (y * w + x) * 4;
                // The red image of the point at d lies at d × red + move.
                pixels[i] = Value(w / 2.0 + (dx - redX) / red, h / 2.0 + (dy - redY) / red);
                pixels[i + 1] = Value(w / 2.0 + dx, h / 2.0 + dy);
                pixels[i + 2] = Value(w / 2.0 + (dx - blueX) / blue, h / 2.0 + (dy - blueY) / blue);
                pixels[i + 3] = 255;
            }
        });
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return bmp;
    }

    private const double HalfDiagonal = 1442.22; // of 2400 × 1600

    /// <summary>The measured moves (pixels) at a position (fractions of the width and height).</summary>
    private static (double RedX, double RedY, double BlueX, double BlueY) MovesAt(ChannelShift shift, float fx, float fy)
    {
        var m = shift.Moves?.At(fx, fy) ?? default;
        return (m.RedX * HalfDiagonal, m.RedY * HalfDiagonal, m.BlueX * HalfDiagonal, m.BlueY * HalfDiagonal);
    }

    [Fact]
    public void Measure_FindsTheRedAndBlueScales()
    {
        using var photo = Scene(1.003, 0.998);
        var shift = ChromaticAberration.Measure(photo)!;
        Assert.InRange(shift.RedScale, 1.0027, 1.0033);
        Assert.InRange(shift.BlueScale, 0.9977, 0.9983);
        foreach (var (fx, fy) in new[] { (0.5f, 0.5f), (0.1f, 0.1f), (0.9f, 0.3f), (0.3f, 0.9f) })
        {
            var m = MovesAt(shift, fx, fy);
            Assert.InRange(Math.Abs(m.RedX) + Math.Abs(m.RedY) + Math.Abs(m.BlueX) + Math.Abs(m.BlueY), 0, 0.25);
        }
        using var clean = Scene(1, 1);
        Assert.Equal(ChannelShift.None, ChromaticAberration.Measure(clean));
    }

    [Fact]
    public void Measure_FindsSubPixelMoves()
    {
        // As the Canon RF 24-105 STM sample: red ≈ 0.3 px larger at the corner around a point off the centre, blue
        // moved by a fifth of a pixel everywhere.
        using var photo = Scene(1.0002, 1, redX: -0.1, redY: -0.15, blueX: 0.12, blueY: 0.18);
        var shift = ChromaticAberration.Measure(photo)!;
        Assert.InRange((shift.RedScale - 1) * HalfDiagonal, 0.2, 0.4);
        Assert.InRange((shift.BlueScale - 1) * HalfDiagonal, -0.07, 0.07);
        foreach (var (fx, fy) in new[] { (0.5f, 0.5f), (0.2f, 0.2f), (0.8f, 0.7f) })
        {
            var m = MovesAt(shift, fx, fy);
            Assert.InRange(m.RedX, -0.17, -0.03);
            Assert.InRange(m.RedY, -0.22, -0.08);
            Assert.InRange(m.BlueX, 0.05, 0.19);
            Assert.InRange(m.BlueY, 0.11, 0.25);
        }
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
