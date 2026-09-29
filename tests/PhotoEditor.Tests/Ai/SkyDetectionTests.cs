using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Presets;
using SkiaSharp;

namespace PhotoEditor.Tests.Ai;

public class ClassProbabilityTests
{
    [Fact]
    public void Softmax_PicksTheLikelyClassPerPixel()
    {
        // 3 classes, 1 × 2 pixels: pixel 0 favours class 2, pixel 1 favours class 0.
        var logits = new Tensor([5, 0, /* class 1 */ 0, 0, /* class 2 */ 9, -5], [1, 3, 1, 2]);
        var p = ImageTensor.ClassProbability(logits, 2);
        Assert.True(p[0] > 0.95f);
        Assert.True(p[1] < 0.01f);
        var sum = ImageTensor.ClassProbability(logits, 0)[0] + ImageTensor.ClassProbability(logits, 1)[0] + p[0];
        Assert.Equal(1f, sum, 4);
    }

    [Fact]
    public void Resample_DoesNotApplyASigmoid()
    {
        var r = ImageTensor.Resample([0.25f, 0.25f, 0.25f, 0.25f], 2, 2, 4, 4);
        Assert.All(r, v => Assert.Equal(0.25f, v, 5));
    }
}

/// <summary>
/// Runs SegFormer on a real outdoor photo when PHOTOEDITOR_MODELS points to the models and PHOTOEDITOR_SAMPLES
/// contains ADE_val_00000001.jpg from the hf-internal-testing/fixtures_ade20k dataset (a stone house under a
/// blue sky on a lawn; not committed, third-party image). Otherwise the tests do nothing. Synthetic drawings
/// are no good here: the model reads flat, texture-free areas as sky.
/// </summary>
public class SkyDetectionModelTests
{
    private static ModelStore? Store()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_MODELS");
        if (dir is null)
            return null;
        var store = new ModelStore(dir, new HttpClient());
        return ModelCatalog.SelectScene.All(store.IsAvailable) ? store : null;
    }

    private static SKBitmap? House()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_SAMPLES");
        var path = dir is null ? null : Path.Combine(dir, "ADE_val_00000001.jpg");
        return path is not null && File.Exists(path) ? SKBitmap.Decode(path) : null;
    }

    [Fact]
    public void DetectsTheSky_AndTheDramaticSkyPresetRuns()
    {
        if (Store() is not { } store || House() is not { } img)
            return;
        using var photo = img;
        using var detector = new AiMaskDetector(store);
        var sky = detector.DetectSky(photo);
        int w = photo.Width, h = photo.Height;
        var coverage = new float[w * h];
        sky.Render(coverage, w, h);
        Assert.True(coverage[(h / 10) * w + w / 10] > 0.8f, "sky, top left");
        Assert.True(coverage[(h * 9 / 10) * w + w / 2] < 0.1f, "lawn, bottom");
        Assert.True(coverage[(h / 2) * w + w / 2] < 0.1f, "house wall, centre");
        Assert.Same(sky, detector.DetectSky(photo));

        var result = PresetEngine.Apply(PresetFactory.BuiltIn[2], EditState.Default, photo, detector);
        Assert.Equal("Sky", Assert.Single(result.State.Masks).Name);
        Assert.DoesNotContain(result.Log, l => l.Contains("skipped"));
    }
}
