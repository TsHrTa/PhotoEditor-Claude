using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Tests.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Adjustments;

public sealed class SofteningTests
{
    /// <summary>Left half: grey 100 with ±12 fine noise; right half: white. A hard edge between them.</summary>
    private static SKBitmap TextureAndEdge()
    {
        var bmp = new SKBitmap(new SKImageInfo(80, 40, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rnd = new Random(7);
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 80; x++)
            {
                byte v = x < 40 ? (byte)(100 + rnd.Next(-12, 13)) : (byte)250;
                bmp.SetPixel(x, y, new SKColor(v, v, v));
            }
        return bmp;
    }

    private static double Std(SKBitmap b, int x0, int x1)
    {
        var v = new List<double>();
        for (int y = 5; y < 35; y++)
            for (int x = x0; x < x1; x++)
                v.Add(b.GetPixel(x, y).Red);
        double mean = v.Average();
        return Math.Sqrt(v.Average(d => (d - mean) * (d - mean)));
    }

    [Fact]
    public void Soften_SmoothsTexture_AndKeepsTheEdge()
    {
        using var src = TextureAndEdge();
        using var soft = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Soften = 100 });
        Assert.True(Std(soft, 5, 33) < Std(src, 5, 33) / 2.5, $"texture {Std(src, 5, 33):0.0} → {Std(soft, 5, 33):0.0}");
        // The edge stays sharp: the pixels right next to it keep their values.
        Assert.InRange(soft.GetPixel(38, 20).Red, 80, 120);
        Assert.InRange(soft.GetPixel(41, 20).Red, 240, 255);
    }

    [Fact]
    public void SoftenInAMask_OnlyChangesTheMaskedArea()
    {
        using var src = TextureAndEdge();
        var state = new EditState
        {
            Masks = [new Mask { Components = [new RectComponent(0, 0, 0.25f, 1)], Adjustments = new AdjustmentSettings { Soften = 100 } }],
        };
        using var result = CpuAdjustmentRenderer.Render(src, state);
        Assert.True(Std(result, 3, 17) < Std(src, 3, 17) / 2.5);
        for (int y = 0; y < 40; y++)
            for (int x = 22; x < 40; x++)
                Assert.Equal(src.GetPixel(x, y), result.GetPixel(x, y));
    }

    [Fact]
    public void SoftenStep_GrowsWithThePhoto()
    {
        Assert.Equal(1, PreparedAdjustments.SoftenStepFor(400));
        Assert.Equal(6, PreparedAdjustments.SoftenStepFor(6000));
    }
}
