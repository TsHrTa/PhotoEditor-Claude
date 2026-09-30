using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public class SharpeningTests
{
    /// <summary>Soft vertical edge (dark → light over a few pixels) plus faint noise on the flat parts.</summary>
    private static SKBitmap SoftEdge()
    {
        var bmp = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rnd = new Random(3);
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            float t = Math.Clamp((x - 17f) / 6f, 0f, 1f);
            byte v = (byte)Math.Clamp(60 + t * 120 + rnd.Next(-2, 3), 0, 255);
            bmp.SetPixel(x, y, new SKColor(v, v, v));
        }
        return bmp;
    }

    private static int Gray(SKBitmap b, int x, int y) => b.GetPixel(x, y).Red;

    [Fact]
    public void Sharpening_IncreasesEdgeContrast()
    {
        using var src = SoftEdge();
        using var sharp = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { SharpenAmount = 100 });
        // Just before the edge gets darker, just after it gets lighter (the unsharp-mask "halo").
        Assert.True(Gray(sharp, 17, 10) < Gray(src, 17, 10) - 3);
        Assert.True(Gray(sharp, 23, 10) > Gray(src, 23, 10) + 3);
    }

    [Fact]
    public void Masking_LeavesFlatAreasAlone()
    {
        using var src = SoftEdge();
        using var sharp = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { SharpenAmount = 150 });
        using var masked = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { SharpenAmount = 150, SharpenMasking = 50 });
        int FlatChange(SKBitmap b)
        {
            int sum = 0;
            for (int x = 2; x < 12; x++)
                sum += Math.Abs(Gray(b, x, 10) - Gray(src, x, 10));
            return sum;
        }
        Assert.True(FlatChange(masked) < FlatChange(sharp) / 2, $"{FlatChange(masked)} vs {FlatChange(sharp)}");
        Assert.True(Gray(masked, 23, 10) > Gray(src, 23, 10) + 3); // edge still sharpened
    }

    [Fact]
    public void ZeroAmount_ChangesNothing()
    {
        using var src = SoftEdge();
        using var result = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { SharpenRadius = 2.5, SharpenMasking = 50 });
        Assert.Equal(0, TestImages.MaxDifference(src, result, out _));
    }

    [Fact]
    public void WithMasks_ShaderMatchesCpu()
    {
        using var src = TestImages.Varied();
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { SharpenAmount = 80 },
            Masks =
            [
                // Sharpening inside a mask is ignored (whole image only); its exposure still applies.
                new Mask
                {
                    Adjustments = new AdjustmentSettings { Exposure = 1, SharpenAmount = 150 },
                    Components = [new RadialGradientComponent()],
                },
            ],
        };
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
    }

    [Fact]
    public void GlobalOnly_ListsTheDetailLensAndToneCurveSliders() =>
        Assert.All(AdjustmentParameters.GlobalOnly, p => Assert.Contains(p.Group,
            new[] { AdjustmentParameters.Detail, AdjustmentParameters.Lens, AdjustmentParameters.ToneCurve }));
}
