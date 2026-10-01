using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Presets;
using SkiaSharp;

namespace PhotoEditor.Tests.Ai;

public class ImageTensorTests
{
    [Fact]
    public void LogitsToCoverage_ResamplesNonSquareOutputs()
    {
        // 4 × 2 logits: top row inside, bottom row outside.
        float[] logits = [9, 9, 9, 9, -9, -9, -9, -9];
        var c = ImageTensor.LogitsToCoverage(logits, 4, 2, 2, 8);
        Assert.True(c[0] > 0.99f);
        Assert.True(c[^1] < 0.01f);
    }
}

/// <summary>Runs BiRefNet when PHOTOEDITOR_MODELS points to a folder containing it (otherwise does nothing).</summary>
public class SubjectDetectionModelTests
{
    private static ModelStore? Store()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_MODELS");
        if (dir is null)
            return null;
        var store = new ModelStore(dir, new HttpClient());
        return ModelCatalog.SelectSubject.All(store.IsAvailable) ? store : null;
    }

    /// <summary>A bright figure-like shape on a busy dark background.</summary>
    private static SKBitmap Scene()
    {
        var bmp = new SKBitmap(new SKImageInfo(480, 640, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        var rnd = new Random(5);
        for (int i = 0; i < 300; i++)
        {
            using var p = new SKPaint { Color = new SKColor((byte)rnd.Next(20, 60), (byte)rnd.Next(20, 70), (byte)rnd.Next(30, 80)) };
            canvas.DrawRect(rnd.Next(480), rnd.Next(640), 40, 40, p);
        }
        using var subject = new SKPaint { Color = new SKColor(230, 120, 60), IsAntialias = true };
        canvas.DrawCircle(240, 200, 70, subject);
        canvas.DrawRoundRect(150, 270, 180, 300, 40, 40, subject);
        return bmp;
    }

    [Fact]
    public void DetectsTheSubject_AndThePortraitPresetBrightensIt()
    {
        if (Store() is not { } store)
            return;
        using var detector = new AiMaskDetector(store);
        using var img = Scene();
        var subject = detector.DetectSubject(img);
        var coverage = new float[480 * 640];
        subject.Render(coverage, 480, 640);
        Assert.True(coverage[400 * 480 + 240] > 0.8f, "body");
        Assert.True(coverage[20 * 480 + 20] < 0.2f, "background corner");
        Assert.Same(subject, detector.DetectSubject(img)); // cached

        var result = PresetEngine.Apply(PresetFactory.BuiltIn[0], EditState.Default, img, detector);
        var mask = Assert.Single(result.State.Masks);
        Assert.Equal("Subject", mask.Name);
        Assert.DoesNotContain(result.Log, l => l.Contains("skipped"));
        double s = PresetEngine.Measure(img, result.State, new Region("Subject"), GoalMetric.Brightness)!.Value;
        double b = PresetEngine.Measure(img, result.State, new Region("Subject", Outside: true), GoalMetric.Brightness)!.Value;
        Assert.True(s >= b * 1.15 - 1, string.Join("\n", result.Log));
    }
}
