using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Ai;

public class RasterMaskComponentTests
{
    /// <summary>Left half covered, right half not.</summary>
    private static RasterMaskComponent LeftHalf(int w = 8, int h = 4)
    {
        var coverage = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w / 2; x++)
            coverage[y * w + x] = 1f;
        return new RasterMaskComponent { MaskPng = RasterMaskComponent.EncodePng(coverage, w, h), Points = [new SelectPoint(0.2f, 0.5f)] };
    }

    [Fact]
    public void Render_ScalesTheRasterOverTheImage()
    {
        var c = LeftHalf();
        var same = new float[8 * 4];
        c.Render(same, 8, 4);
        Assert.Equal(1f, same[0]);
        Assert.Equal(0f, same[7]);

        var big = new float[80 * 40];
        c.Render(big, 80, 40);
        Assert.Equal(1f, big[20 * 80 + 5], 2);
        Assert.Equal(0f, big[20 * 80 + 75], 2);
    }

    [Fact]
    public void Empty_RendersNothing()
    {
        var coverage = Enumerable.Repeat(1f, 16).ToArray();
        new RasterMaskComponent().Render(coverage, 4, 4);
        Assert.All(coverage, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Sidecar_RoundTripsRasterMasks()
    {
        var state = new EditState { Masks = [new Mask { Name = "Object", Components = [LeftHalf() with { Box = new SelectBox(0.1f, 0.1f, 0.5f, 0.9f) }] }] };
        var json = SidecarFile.Serialize(EditDocument.From(state));
        Assert.Contains("\"type\": \"raster\"", json);
        var loaded = SidecarFile.Deserialize(json).ToState();
        Assert.Equal(state, loaded);
        Assert.Equal("Object", loaded.Masks[0].Components[0].DisplayName);
    }

    [Fact]
    public void UpscaleLogits_KeepsTheInsideAndSmoothsEdges()
    {
        // 2 × 2 logits: left column inside, right column outside.
        var result = SegmentAnything.UpscaleLogits([10, -10, 10, -10], 2, 8, 4);
        Assert.True(result[0] > 0.99f);
        Assert.True(result[7] < 0.01f);
        Assert.InRange(result[4], 0.01f, 0.99f); // the middle is a soft edge
    }

    [Fact]
    public void Preprocess_NormalisesToTheModelInput()
    {
        using var bmp = new SKBitmap(new SKImageInfo(10, 6, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(new SKColor(255, 0, 0));
        var t = SegmentAnything.Preprocess(bmp);
        Assert.Equal([1L, 3, 1024, 1024], t.Shape);
        Assert.Equal((1f - 0.485f) / 0.229f, t.Data[0], 3);              // red plane
        Assert.Equal((0f - 0.456f) / 0.224f, t.Data[1024 * 1024], 3);    // green plane
    }
}

/// <summary>
/// Runs the real SAM 2.1 model when it has been downloaded; set PHOTOEDITOR_MODELS to the model folder
/// (the test passes without doing anything when the models are not there).
/// </summary>
public class SegmentAnythingModelTests
{
    private static ModelStore? Store()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_MODELS");
        if (dir is null)
            return null;
        var store = new ModelStore(dir, new HttpClient());
        return ModelCatalog.SelectObject.All(store.IsAvailable) ? store : null;
    }

    [Fact]
    public void ClickOnADisc_SelectsTheDisc()
    {
        if (Store() is not { } store)
            return;
        using var sam = SegmentAnything.Load(store, InferenceDevice.Cpu);
        using var bmp = new SKBitmap(new SKImageInfo(600, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(30, 60, 30));
            using var paint = new SKPaint { Color = new SKColor(240, 200, 40), IsAntialias = true };
            canvas.DrawCircle(200, 200, 90, paint);
        }
        var embedding = sam.Encode(bmp);
        var mask = sam.Select(embedding, [new SelectPoint(200f / 600, 0.5f)], null, bmp.Width, bmp.Height);

        var coverage = new float[600 * 400];
        mask.Render(coverage, 600, 400);
        Assert.True(coverage[200 * 600 + 200] > 0.9f, "centre of the disc");
        Assert.True(coverage[200 * 600 + 500] < 0.1f, "background");
        // Box on the disc gives the same object.
        var boxed = sam.Select(embedding, [], new SelectBox(100f / 600, 100f / 400, 300f / 600, 300f / 400), bmp.Width, bmp.Height);
        boxed.Render(coverage, 600, 400);
        Assert.True(coverage[200 * 600 + 200] > 0.9f);
        Assert.True(coverage[50 * 600 + 550] < 0.1f);
    }
}
