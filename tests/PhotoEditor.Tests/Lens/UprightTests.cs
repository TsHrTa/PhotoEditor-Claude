using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.Tests.Lens;

public class UprightTests
{
    private const int W = 720, H = 480;

    /// <summary>A "facade": dark wall with light window frames (vertical and horizontal edges).</summary>
    private static SKBitmap Facade()
    {
        var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(60, 55, 50));
        using var paint = new SKPaint { Color = new SKColor(210, 205, 195), IsAntialias = true };
        for (int x = 40; x < W - 60; x += 90)
            for (int y = 40; y < H - 60; y += 110)
                canvas.DrawRect(x, y, 50, 70, paint);
        using var line = new SKPaint { Color = new SKColor(230, 230, 230), StrokeWidth = 4, IsAntialias = true };
        canvas.DrawLine(20, 10, 20, H - 10, line);
        canvas.DrawLine(W - 20, 10, W - 20, H - 10, line);
        return bmp;
    }

    private static SKBitmap Render(SKBitmap photo, AdjustmentSettings settings) =>
        CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = settings });

    /// <summary>Length-weighted mean |tilt| (degrees) of the long near-vertical edges.</summary>
    private static double VerticalTilt(SKBitmap photo)
    {
        var segments = Upright.Detect(photo).Where(s => s.Length > 40).ToList();
        double sum = 0, weight = 0;
        foreach (var s in segments)
        {
            double tilt = Math.Atan2(s.X2 - s.X1, s.Y2 - s.Y1) * 180 / Math.PI;
            tilt = tilt > 90 ? tilt - 180 : tilt < -90 ? tilt + 180 : tilt;
            if (Math.Abs(tilt) > 25)
                continue;
            sum += Math.Abs(tilt) * s.Length;
            weight += s.Length;
        }
        Assert.True(weight > 0, "no vertical edges found");
        return sum / weight;
    }

    [Fact]
    public void Detect_FindsTheEdges()
    {
        using var photo = Facade();
        var segments = Upright.Detect(photo);
        // 7 × 4 windows with 4 sides each, plus the two long lines (each line has two edges).
        Assert.True(segments.Count >= 60, $"{segments.Count} segments");
        Assert.Contains(segments, s => s.Length > 400 && Math.Abs(s.X1 - s.X2) < 2);
        Assert.Empty(Upright.Detect(TestImages.Solid(new SKColor(128, 128, 128), 200)));
    }

    [Fact]
    public void Level_UndoesARotation()
    {
        using var photo = Facade();
        using var tilted = Render(photo, new AdjustmentSettings { TransformRotate = 4 });
        var result = Upright.Estimate(tilted, UprightMode.Level)!.Value;
        Assert.Equal(0, result.Vertical);
        Assert.Equal(0, result.Horizontal);
        Assert.InRange(result.Rotate, -4.4, -3.6);
    }

    [Theory]
    [InlineData(UprightMode.Vertical)]
    [InlineData(UprightMode.Auto)]
    [InlineData(UprightMode.Full)]
    public void ConvergingVerticals_AreMadeParallel(UprightMode mode)
    {
        using var photo = Facade();
        using var leaning = Render(photo, new AdjustmentSettings { TransformVertical = 40, TransformRotate = 1.5 });
        Assert.True(VerticalTilt(leaning) > 2);
        var (v, h, r) = Upright.Estimate(leaning, mode)!.Value;
        Assert.True(v < -15, $"vertical {v}");
        using var fixedPhoto = Render(leaning, new AdjustmentSettings { TransformVertical = v, TransformHorizontal = h, TransformRotate = r });
        Assert.True(VerticalTilt(fixedPhoto) < 0.6, $"still tilted {VerticalTilt(fixedPhoto):0.00}° with {v}, {h}, {r}");
    }

    [Fact]
    public void NoEdges_GivesNothing()
    {
        using var flat = TestImages.Solid(new SKColor(100, 120, 140), 300);
        Assert.Null(Upright.Estimate(flat, UprightMode.Auto));
    }
}
