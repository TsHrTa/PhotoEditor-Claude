using PhotoEditor.Core.Ai;
using SkiaSharp;

namespace PhotoEditor.Tests.Ai;

public sealed class RestorePipelineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("restore-pipeline-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SKBitmap Solid(byte v)
    {
        var bmp = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(new SKColor(v, v, v));
        return bmp;
    }

    [Fact]
    public void Working_BlendsCachedCopies_AndDeblursTheDenoisedOne()
    {
        var photo = Path.Combine(_dir, "p.jpg");
        File.WriteAllBytes(photo, [1]);
        var cache = new RestoreCache(Path.Combine(_dir, "cache"));
        using (var denoised = Solid(200)) cache.Save(photo, "denoise", denoised);
        using (var deblurred = Solid(100)) cache.Save(photo, "deblur", deblurred);
        using (var both = Solid(0)) cache.Save(photo, "deblur-of-denoised", both);
        // No models: everything must come from the cache.
        using var pipeline = new RestorePipeline(new ModelStore(Path.Combine(_dir, "models"), new HttpClient()), cache);
        using var original = Solid(0);

        Assert.Same(original, pipeline.Working(photo, original, 0, 0, null, default));
        using (var half = pipeline.Working(photo, original, 0.5, 0, null, default))
            Assert.Equal(100, half.GetPixel(3, 3).Red);
        using (var deblur = pipeline.Working(photo, original, 0, 1, null, default))
            Assert.Equal(100, deblur.GetPixel(3, 3).Red);
        // Denoise 100 % then deblur 50 % of the deblurred-denoised copy: 200 → halfway to 0.
        using (var combined = pipeline.Working(photo, original, 1, 0.5, null, default))
            Assert.Equal(100, combined.GetPixel(3, 3).Red);
    }

    [Fact]
    public void Variants_AreNamedAsTheCacheExpects()
    {
        Assert.Equal("denoise", RestorePipeline.Variant(RestoreKind.Denoise, false));
        Assert.Equal("deblur", RestorePipeline.Variant(RestoreKind.Deblur, false));
        Assert.Equal("deblur-of-denoised", RestorePipeline.Variant(RestoreKind.Deblur, true));
    }
}
