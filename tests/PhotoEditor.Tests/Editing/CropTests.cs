using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using SkiaSharp;

namespace PhotoEditor.Tests.Editing;

public class CropTests
{
    private const int W = 400, H = 200;

    private static Crop Rect(double l, double t, double r, double b, double angle = 0) =>
        new() { Left = l, Top = t, Right = r, Bottom = b, Angle = angle };

    [Fact]
    public void Default_IsWholeImage()
    {
        Assert.True(Crop.None.IsDefault);
        Assert.Equal((W, H), Crop.None.OutputSize(W, H));
        Assert.True(Crop.None.Frame(W, H).IsInside(W, H));
    }

    [Fact]
    public void Frame_RoundTripsLocalAndImageCoordinates()
    {
        var f = Rect(0.2, 0.1, 0.8, 0.9, 12).Frame(W, H);
        var (x, y) = f.ToImage(30, -20);
        var (u, v) = f.ToLocal(x, y);
        Assert.Equal(30, u, 9);
        Assert.Equal(-20, v, 9);
        Assert.Equal(f.ToImage(0, 0), (f.CenterX, f.CenterY));
    }

    [Fact]
    public void Drag_Corner_KeepsOppositeCorner()
    {
        var start = Rect(0.1, 0.1, 0.9, 0.9);
        var c = CropGeometry.Drag(start, CropHandle.TopLeft, (0.1, 0.1), (0.3, 0.2), null, W, H);
        Assert.Equal(0.3, c.Left, 9);
        Assert.Equal(0.2, c.Top, 9);
        Assert.Equal(0.9, c.Right, 9);
        Assert.Equal(0.9, c.Bottom, 9);
    }

    [Fact]
    public void Drag_Edge_StopsAtImageBorder()
    {
        var c = CropGeometry.Drag(Rect(0.1, 0.1, 0.9, 0.9), CropHandle.Right, (0.9, 0.5), (1.5, 0.5), null, W, H);
        Assert.Equal(1.0, c.Right, 6);
        Assert.Equal(0.1, c.Left, 9);
    }

    [Fact]
    public void Drag_Move_SlidesAlongBorder()
    {
        var c = CropGeometry.Drag(Rect(0.2, 0.2, 0.6, 0.6), CropHandle.Move, (0.4, 0.4), (0.3, -0.5), null, W, H);
        Assert.Equal(0.1, c.Left, 6);   // x moved freely
        Assert.Equal(0.0, c.Top, 6);    // y stopped at the top
        Assert.Equal(0.4, c.Right - c.Left, 9);
        Assert.Equal(0.4, c.Bottom - c.Top, 9);
    }

    [Fact]
    public void Drag_WithAspect_KeepsRatio()
    {
        var start = CropGeometry.WithAspect(Crop.None, 1.0, W, H); // square, 200 × 200 px
        var c = CropGeometry.Drag(start, CropHandle.BottomRight, (0.75, 1.0), (0.6, 0.7), 1.0, W, H);
        var (w, h) = c.OutputSize(W, H);
        Assert.Equal(w, h);
        Assert.True(w < 200);
        Assert.True(c.Frame(W, H).IsInside(W, H));
    }

    [Fact]
    public void Drag_Edge_RespectsMinimumSize()
    {
        var c = CropGeometry.Drag(Rect(0.1, 0.1, 0.9, 0.9), CropHandle.Left, (0.1, 0.5), (2.0, 0.5), null, W, H);
        Assert.True(c.Right - c.Left > 0);
        Assert.Equal(0.9, c.Right, 9);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-30)]
    [InlineData(45)]
    public void WithAngle_ShrinksToStayInside(double angle)
    {
        var c = CropGeometry.WithAngle(Crop.None, angle, W, H);
        Assert.Equal(angle, c.Angle);
        var f = c.Frame(W, H);
        Assert.True(f.IsInside(W, H));
        Assert.Equal(2.0, f.HalfWidth / f.HalfHeight, 6); // aspect kept
        Assert.True(f.HalfWidth < W / 2.0);
        // Tight: a slightly larger frame would leave the image.
        Assert.False((f with { HalfWidth = f.HalfWidth * 1.01, HalfHeight = f.HalfHeight * 1.01 }).IsInside(W, H));
    }

    [Fact]
    public void WithAngle_IsClamped() => Assert.Equal(Crop.MaxAngle, CropGeometry.WithAngle(Crop.None, 90, W, H).Angle);

    [Fact]
    public void WithAspect_FitsInsideCurrentCrop()
    {
        var c = CropGeometry.WithAspect(Crop.None, 4.0 / 5.0, W, H);
        var (w, h) = c.OutputSize(W, H);
        Assert.Equal(160, w);
        Assert.Equal(200, h);
        Assert.Equal(0.5, (c.Left + c.Right) / 2, 9);
    }

    [Fact]
    public void Normalize_RejectsGarbage()
    {
        Assert.Equal(Crop.None, CropGeometry.Normalize(null));
        Assert.Equal(Crop.None, CropGeometry.Normalize(Rect(0.5, 0, 0.2, 1)));
        Assert.Equal(Crop.None, CropGeometry.Normalize(Rect(double.NaN, 0, 1, 1)));
        Assert.Equal(Crop.MaxAngle, CropGeometry.Normalize(Rect(0, 0, 1, 1, 80)).Angle);
    }

    [Fact]
    public void ApplyCrop_CutsTheRightPixels()
    {
        // Left half red, right half blue; crop the right quarter.
        using var src = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(src))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(20, 0, 20, 20, paint);
        }
        using var cropped = CpuAdjustmentRenderer.ApplyCrop(src, Rect(0.75, 0, 1, 1));
        Assert.Equal(10, cropped.Width);
        Assert.Equal(20, cropped.Height);
        Assert.Equal(SKColors.Blue, cropped.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, cropped.GetPixel(9, 19));
    }

    [Fact]
    public void ApplyCrop_Rotated_HasNoTransparentEdges()
    {
        using var src = TestImages.Solid(SKColors.Green, 100);
        var crop = CropGeometry.WithAngle(Crop.None, 20, 100, 100);
        using var cropped = CpuAdjustmentRenderer.ApplyCrop(src, crop);
        Assert.Equal(SKColors.Green, cropped.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, cropped.GetPixel(cropped.Width - 1, cropped.Height - 1));
    }

    [Fact]
    public void Vignette_FollowsCropFrame_ShaderMatchesCpu()
    {
        using var src = TestImages.Varied(withAlpha: false);
        var crop = CropGeometry.WithAngle(Rect(0.1, 0.2, 0.7, 0.9), 15, src.Width, src.Height);
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { VignetteAmount = -90, VignetteRoundness = 30 },
            Crop = crop,
        };
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");

        // Centre of the crop is unaffected; it differs from the uncropped vignette.
        using var uncropped = CpuAdjustmentRenderer.Render(src, state with { Crop = Crop.None });
        Assert.True(TestImages.MaxDifference(cpu, uncropped, out _) > 2);
        var f = crop.Frame(src.Width, src.Height);
        int cx = (int)f.CenterX, cy = (int)f.CenterY;
        Assert.Equal(src.GetPixel(cx, cy), cpu.GetPixel(cx, cy));
    }

    [Fact]
    public void Sidecar_RoundTripsCrop()
    {
        var state = new EditState { Crop = Rect(0.1, 0.2, 0.7, 0.9, -7.5) };
        var json = SidecarFile.Serialize(EditDocument.From(state));
        Assert.Equal(state, SidecarFile.Deserialize(json).ToState());
        Assert.Equal(Crop.None, SidecarFile.Deserialize("""{ "version": 1 }""").Crop);
    }
}
