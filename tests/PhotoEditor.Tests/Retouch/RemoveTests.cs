using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;
using SkiaSharp;

namespace PhotoEditor.Tests.Retouch;

[Collection(nameof(FillStoreCollection))]
public sealed class RemoveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fills-" + Guid.NewGuid().ToString("N"));

    public RemoveTests() => FillStore.Directory = _dir;

    public void Dispose()
    {
        FillStore.Directory = FillStore.DefaultDirectory;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>400 × 300 textured wall (a gradient with fine texture) with a dark vertical pole at x = 200.</summary>
    private static SKBitmap WallWithPole()
    {
        var bmp = new SKBitmap(new SKImageInfo(400, 300, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
            {
                double v = 150 + y * 0.1 + 8 * Math.Sin(x * 0.9) * Math.Sin(y * 0.7);
                if (Math.Abs(x - 200) < 5 && y > 60 && y < 240)
                    v = 25;
                byte b = (byte)Math.Clamp(v, 0, 255);
                bmp.SetPixel(x, y, new SKColor(b, (byte)(b * 0.9), (byte)(b * 0.8)));
            }
        return bmp;
    }

    private static Spot PoleSpot() => new()
    {
        Mode = SpotMode.Remove,
        Radius = 9f / 400,
        Path = [new BrushPoint(200f / 400, 55f / 300), new BrushPoint(200f / 400, 150f / 300), new BrushPoint(200f / 400, 245f / 300)],
    };

    [Fact]
    public void Geometry_ScalesWithTheImage()
    {
        var spot = PoleSpot();
        var small = RemoveFill.ContextRect(spot, 400, 300);
        var large = RemoveFill.ContextRect(spot, 1600, 1200);
        Assert.Equal(small.Left * 4, large.Left, 2);
        Assert.Equal(small.Width * 4, large.Width, 2);
        Assert.Equal(small.Width, small.Height);
        Assert.True(small.Left >= 0 && small.Bottom <= 300);
        var (points, radius) = RemoveFill.Stroke(spot, 400, 300);
        Assert.Equal(1f, RemoveFill.Coverage(points, radius, 200, 150));
        Assert.Equal(0f, RemoveFill.Coverage(points, radius, 215, 150));
        Assert.InRange(RemoveFill.Coverage(points, radius, 208, 150), 0.01f, 0.99f); // soft edge
    }

    [Fact]
    public void Apply_BlendsTheStoredFill_InsideTheStrokeOnly()
    {
        using var photo = WallWithPole();
        // A fill that is plain mid-grey everywhere: inside the stroke the pole becomes grey.
        var grey = new SKBitmap(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul));
        grey.Erase(new SKColor(120, 120, 120));
        var spot = PoleSpot() with { Fill = FillStore.Save(grey) };
        using var result = Retouching.Apply(photo, [spot]);
        Assert.Equal(new SKColor(120, 120, 120), result.GetPixel(200, 150));
        Assert.Equal(photo.GetPixel(230, 150), result.GetPixel(230, 150));
        Assert.Equal(photo.GetPixel(200, 20), result.GetPixel(200, 20));
        // Without its fill a Remove spot changes nothing.
        using var none = Retouching.Apply(photo, [spot with { Fill = null }]);
        Assert.Equal(0, TestImages.MaxDifference(photo, none, out _));
        // The headroom layers get nothing extra where the object was removed.
        var above = new SKBitmap(new SKImageInfo(400, 300, SKColorType.Rgba8888, SKAlphaType.Opaque));
        above.Erase(new SKColor(90, 90, 90));
        var fine = new SKBitmap(new SKImageInfo(400, 300, SKColorType.Rgba8888, SKAlphaType.Opaque));
        fine.Erase(new SKColor(200, 200, 200));
        Headroom.Attach(photo, new Headroom(above, 0.3f, fine));
        using var layered = Retouching.Apply(photo, [spot]);
        var h = Headroom.Of(layered)!;
        Assert.Equal(0, h.Bitmap.GetPixel(200, 150).Red);
        Assert.Equal(128, h.Fine!.GetPixel(200, 150).Red);
        Assert.Equal(90, h.Bitmap.GetPixel(230, 150).Red);
        // The incremental canvas gives the same pixels.
        using var image = SKImage.FromBitmap(photo);
        Headroom.Attach(image, Headroom.Of(photo));
        using var canvas = new RetouchCanvas(image);
        canvas.Update([spot]);
        Assert.Equal(0, TestImages.MaxDifference(layered, canvas.Bitmap, out _));
    }

    [Fact]
    public void Sidecar_KeepsThePathAndTheFill()
    {
        var spot = PoleSpot() with { Fill = "abc123" };
        var state = new EditState { Spots = [spot] };
        var read = SidecarFile.Deserialize(SidecarFile.Serialize(EditDocument.From(state))).ToState();
        Assert.Equal(state, read);
        Assert.Equal(3, read.Spots[0].Path.Count);
    }

    /// <summary>
    /// A selection built from several strokes (user request: paint more, erase parts, then remove): the pole plus a
    /// separate dot, with the pole's middle erased. Coverage, the model's hole, the bounds and the blended fill all
    /// follow it, and the sidecar keeps the strokes.
    /// </summary>
    [Fact]
    public void Selection_OfSeveralStrokes_AddsAndErases()
    {
        var spot = new Spot
        {
            Mode = SpotMode.Remove,
            Strokes =
            [
                new RemoveStroke([new BrushPoint(200f / 400, 55f / 300), new BrushPoint(200f / 400, 245f / 300)], 9f / 400),
                new RemoveStroke([new BrushPoint(100f / 400, 100f / 300)], 6f / 400),
                new RemoveStroke([new BrushPoint(200f / 400, 150f / 300)], 12f / 400, Erase: true),
            ],
        };
        var strokes = RemoveFill.Strokes(spot, 400, 300);
        Assert.Equal(1f, RemoveFill.Coverage(strokes, 200, 80));
        Assert.Equal(1f, RemoveFill.Coverage(strokes, 100, 100));
        Assert.Equal(0f, RemoveFill.Coverage(strokes, 200, 150)); // erased
        Assert.Equal(0f, RemoveFill.Coverage(strokes, 150, 100));
        var bounds = RemoveFill.Bounds(spot, 400, 300);
        Assert.True(bounds.Left <= 94 && bounds.Right >= 209 && bounds.Top <= 46 && bounds.Bottom >= 254, bounds.ToString());

        using var photo = WallWithPole();
        var (image, hole) = RemoveFill.ModelInput(photo, spot);
        image.Dispose();
        var rect = RemoveFill.ContextRect(spot, 400, 300);
        bool HoleAt(float x, float y)
        {
            float s = RemoveFill.ModelSize / rect.Width;
            return hole[(int)((y - rect.Top) * s) * RemoveFill.ModelSize + (int)((x - rect.Left) * s)];
        }
        Assert.True(HoleAt(200, 80) && HoleAt(100, 100));
        Assert.False(HoleAt(200, 150));
        Assert.False(HoleAt(150, 100));

        var grey = new SKBitmap(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul));
        grey.Erase(new SKColor(120, 120, 120));
        using var result = Retouching.Apply(photo, [spot with { Fill = FillStore.Save(grey) }]);
        Assert.Equal(new SKColor(120, 120, 120), result.GetPixel(200, 80));
        Assert.Equal(photo.GetPixel(200, 150), result.GetPixel(200, 150)); // the erased part keeps the photo

        var state = new EditState { Spots = [spot] };
        var json = SidecarFile.Serialize(EditDocument.From(state));
        Assert.DoesNotContain("removeStrokes", json);
        Assert.Equal(state, SidecarFile.Deserialize(json).ToState());
    }

    /// <summary>The real model when PHOTOEDITOR_MODELS points to a folder with lama/lama_fp32.onnx.</summary>
    [Fact]
    public void Lama_RemovesThePole()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_MODELS");
        var path = dir is null ? null : Path.Combine(dir, ModelCatalog.Inpaint.Id);
        if (path is null || !File.Exists(path))
            return;
        using var photo = WallWithPole();
        using var inpainter = Inpainter.Load(path);
        var spot = PoleSpot();
        spot = spot with { Fill = inpainter.FillSpot(photo, spot) };
        using var removed = CpuAdjustmentRenderer.Render(photo, new EditState { Spots = [spot] });
        // Where the pole was: as bright as the wall around it, no dark pixels left.
        for (int y = 70; y < 230; y += 10)
        {
            int darkest = Enumerable.Range(194, 13).Min(x => (int)removed.GetPixel(x, y).Red);
            double wall = 150 + y * 0.1;
            Assert.True(darkest > wall - 30, $"y {y}: darkest {darkest} vs wall {wall:F0}");
        }
    }
}

[CollectionDefinition(nameof(FillStoreCollection), DisableParallelization = true)]
public sealed class FillStoreCollection;
