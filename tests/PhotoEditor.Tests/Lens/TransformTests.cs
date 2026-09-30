using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.Tests.Lens;

/// <summary>The Transform panel: the projective map, the warp, and settings round trips.</summary>
public class TransformTests
{
    private const int W = 600, H = 400;

    [Fact]
    public void Matrix_InverseAndProduct()
    {
        var m = Matrix3.Translate(12, -7) * Matrix3.Rotate(17) * new Matrix3(1, 0.1, 0, 0.05, 1.2, 0, 1e-4, -2e-4, 1);
        var (x, y) = m.Apply(30, -40);
        var (bx, by) = m.Inverse().Apply(x, y);
        Assert.Equal(30, bx, 9);
        Assert.Equal(-40, by, 9);
        Assert.Equal((0.0, 1.0), Rounded(Matrix3.Rotate(90).Apply(1, 0))); // clockwise on screen: right → down
    }

    private static (double, double) Rounded((double X, double Y) p) => (Math.Round(p.X, 9) + 0.0, Math.Round(p.Y, 9) + 0.0);

    [Fact]
    public void Default_IsNoTransform()
    {
        Assert.Null(Perspective.For(AdjustmentSettings.Default, W, H));
        Assert.Null(LensSetup.For(PhotoLens.Unknown, AdjustmentSettings.Default, W, H));
        Assert.NotNull(LensSetup.For(PhotoLens.Unknown, new AdjustmentSettings { TransformScale = 90 }, W, H));
    }

    /// <summary>Width of the photo an output row shows (Fill maps output → photo).</summary>
    private static double SourceWidthAt(Perspective p, double y)
    {
        var (l, _) = p.Fill.Apply(-W / 2.0, y);
        var (r, _) = p.Fill.Apply(W / 2.0, y);
        return r - l;
    }

    [Fact]
    public void NegativeVertical_WidensTheTop()
    {
        // Widening the top means the top output row shows less of the photo than the bottom row.
        var p = Perspective.For(new AdjustmentSettings { TransformVertical = -50 }, W, H)!;
        Assert.True(SourceWidthAt(p, -H / 2.0) < SourceWidthAt(p, H / 2.0) * 0.95);
        var q = Perspective.For(new AdjustmentSettings { TransformVertical = 50 }, W, H)!;
        Assert.True(SourceWidthAt(q, -H / 2.0) > SourceWidthAt(q, H / 2.0) * 1.05);
        // The centre stays.
        var (cx, cy) = p.Full.Apply(0, 0);
        Assert.Equal(0, cx, 6);
        Assert.Equal(0, cy, 6);
    }

    [Fact]
    public void Vertical_StraightensConvergingLines()
    {
        // Two lines that lean together towards the top, as a tall building photographed from below.
        using var photo = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(photo))
        using (var pen = new SKPaint { Color = SKColors.White, StrokeWidth = 5, IsAntialias = true })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawLine(200, 0, 160, H, pen);
            canvas.DrawLine(400, 0, 440, H, pen);
        }
        double Spread(SKBitmap b, int y)
        {
            // Distance between the two lines along a row (centres of the bright runs left and right of the middle).
            double Centre(int from, int to)
            {
                double sum = 0, weight = 0;
                for (int x = from; x < to; x++)
                {
                    double v = b.GetPixel(x, y).Red;
                    sum += v * x;
                    weight += v;
                }
                return sum / weight;
            }
            return Centre(W / 2, W) - Centre(0, W / 2);
        }
        double before = Spread(photo, 20) / Spread(photo, H - 20);
        Assert.True(before < 0.85);
        var best = double.MaxValue;
        foreach (double v in new[] { -20.0, -40, -60 })
        {
            using var result = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = new AdjustmentSettings { TransformVertical = v } });
            best = Math.Min(best, Math.Abs(Spread(result, 20) / Spread(result, H - 20) - 1));
        }
        Assert.True(best < 0.05, $"lines still converge: {best:0.000}");
    }

    [Fact]
    public void Result_FillsTheFrame_AndSmallScaleShowsWhite()
    {
        using var grey = TestImages.Solid(new SKColor(90, 90, 90), 1);
        using var photo = grey.Resize(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul), SKSamplingOptions.Default);
        using var filled = CpuAdjustmentRenderer.Render(photo, new EditState
        {
            Adjustments = new AdjustmentSettings { TransformVertical = -60, TransformRotate = 6, TransformAspect = 30, TransformOffsetX = 0 },
        });
        foreach (var (x, y) in new[] { (0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1), (W / 2, 0), (0, H / 2) })
            Assert.InRange(filled.GetPixel(x, y).Red, 85, 95);
        using var small = CpuAdjustmentRenderer.Render(photo, new EditState { Adjustments = new AdjustmentSettings { TransformScale = 70 } });
        Assert.Equal(SKColors.White, small.GetPixel(2, 2));
        Assert.InRange(small.GetPixel(W / 2, H / 2).Red, 85, 95);
        // Offsets move the picture: with a smaller scale, a large X offset shows white on the left.
        using var moved = CpuAdjustmentRenderer.Render(photo, new EditState
        {
            Adjustments = new AdjustmentSettings { TransformScale = 90, TransformOffsetX = 100 },
        });
        Assert.Equal(SKColors.White, moved.GetPixel(5, H / 2));
        Assert.InRange(moved.GetPixel(W - 5, H / 2).Red, 85, 95);
    }

    [Fact]
    public void Transform_ShaderMatchesCpu()
    {
        using var photo = TestImages.Varied(120, 80, withAlpha: false);
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { TransformVertical = -35, TransformHorizontal = 25, TransformRotate = -4, Exposure = 0.3 },
        };
        using var cpu = CpuAdjustmentRenderer.Render(photo, state);
        using var gpu = AdjustmentShader.RenderRaster(photo, state);
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
    }

    [Fact]
    public void Xmp_AndCopy_RoundTrip()
    {
        var a = new AdjustmentSettings
        {
            TransformVertical = -35, TransformHorizontal = 12, TransformRotate = 1.5, TransformAspect = -20,
            TransformScale = 110, TransformOffsetX = 4.5, TransformOffsetY = -7,
        };
        var geometry = new ImageGeometry(W, H);
        var xml = LightroomXmp.Write(new EditState { Adjustments = a }, geometry, null, out _);
        Assert.Contains("crs:PerspectiveVertical=\"-35\"", xml);
        Assert.Contains("crs:PerspectiveRotate=\"+1.5\"", xml);
        Assert.Equal(a, LightroomXmp.Read(xml, geometry).Adjustments);
        Assert.Equal(AdjustmentSettings.Default, LightroomXmp.Read(LightroomXmp.Write(EditState.Default, geometry, null, out _), geometry).Adjustments);

        var source = new EditState { Adjustments = a };
        Assert.Equal(a, SettingsTransfer.Apply(EditState.Default, source, SettingsGroups.Transform, W, H).Adjustments);
        Assert.Equal(AdjustmentSettings.Default, SettingsTransfer.Apply(EditState.Default, source, SettingsGroups.Default, W, H).Adjustments);
        Assert.Equal(a, SidecarFile.Deserialize(SidecarFile.Serialize(EditDocument.From(source))).Adjustments);
    }
}
