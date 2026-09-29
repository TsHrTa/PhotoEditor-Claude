using PhotoEditor.Core.Ai;
using SkiaSharp;

namespace PhotoEditor.Tests.Ai;

public sealed class DenoiserTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-denoise-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SKBitmap Noisy(int w, int h, int seed = 1, int noise = 40)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte V(int basis) => (byte)Math.Clamp(basis + rnd.Next(-noise, noise + 1), 0, 255);
            bmp.SetPixel(x, y, new SKColor(V(120 + x % 50), V(100), V(80 + y % 60)));
        }
        return bmp;
    }

    [Theory]
    [InlineData(37, 21)]     // smaller than one window, not divisible by 8
    [InlineData(1100, 700)]  // several tiles with partial ones at the edges
    public void ProcessTiles_WithAPassThroughModel_ReproducesTheImage(int w, int h)
    {
        using var img = Noisy(w, h);
        int calls = 0;
        using var result = Denoiser.ProcessTiles(img, t =>
        {
            calls++;
            Assert.Equal(0, t.Shape[2] % 8);
            Assert.Equal(0, t.Shape[3] % 8);
            return t;
        });
        int diff = TestImages.MaxDifference(img, result, out var at);
        Assert.True(diff == 0, $"difference {diff} at {at}");
        int core = Denoiser.Window - 2 * Denoiser.Margin;
        Assert.Equal(((w + core - 1) / core) * ((h + core - 1) / core), calls);
    }

    [Fact]
    public void ProcessTiles_ReportsProgress_AndCanBeCancelled()
    {
        using var img = Noisy(1000, 500);
        var reports = new List<TileProgress>();
        using (Denoiser.ProcessTiles(img, t => t, new SyncProgress(reports.Add))) { }
        Assert.Equal(reports.Count, reports[^1].Total);
        Assert.Equal(1.0, reports[^1].Fraction);

        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            Denoiser.ProcessTiles(img, t => { cts.Cancel(); return t; }, null, cts.Token));
    }

    [Fact]
    public void Blend_InterpolatesPixels()
    {
        using var a = TestImages.Solid(new SKColor(0, 100, 200));
        using var b = TestImages.Solid(new SKColor(200, 100, 0));
        using var half = Denoiser.Blend(a, b, 0.5);
        Assert.Equal(new SKColor(100, 100, 100), half.GetPixel(1, 1));
        using var none = Denoiser.Blend(a, b, 0);
        Assert.Equal(0, TestImages.MaxDifference(a, none, out _));
    }

    [Fact]
    public void Cache_RoundTripsAndChangesWithThePhoto()
    {
        var photo = Path.Combine(_dir, "p.jpg");
        File.WriteAllText(photo, "v1");
        var cache = new DenoiseCache(Path.Combine(_dir, "cache"));
        using var img = Noisy(20, 10);
        Assert.Null(cache.Load(photo, 20, 10));
        cache.Save(photo, img);
        using (var loaded = cache.Load(photo, 20, 10))
            Assert.Equal(0, TestImages.MaxDifference(img, loaded!, out _));
        Assert.Null(cache.Load(photo, 21, 10)); // size mismatch → recompute

        var before = cache.PathFor(photo);
        File.WriteAllText(photo, "v2 (edited elsewhere)");
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(before, cache.PathFor(photo));
    }

    private sealed class SyncProgress(Action<TileProgress> report) : IProgress<TileProgress>
    {
        public void Report(TileProgress value) => report(value);
    }

    /// <summary>Runs SCUNet when PHOTOEDITOR_MODELS contains it (otherwise does nothing).</summary>
    [Fact]
    public void RealModel_RemovesNoise()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_MODELS");
        if (dir is null)
            return;
        var store = new ModelStore(dir, new HttpClient());
        if (!ModelCatalog.Denoise.All(store.IsAvailable))
            return;
        using var denoiser = Denoiser.Load(store, InferenceDevice.Cpu);
        // Flat grey plus strong noise: the result should be much flatter.
        var bmp = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rnd = new Random(3);
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++)
        {
            byte v = (byte)Math.Clamp(128 + (int)(rnd.NextDouble() * 60 - 30), 0, 255);
            bmp.SetPixel(x, y, new SKColor(v, v, v));
        }
        using var noisy = bmp;
        using var clean = denoiser.Denoise(noisy);
        static double Std(SKBitmap b)
        {
            var values = Enumerable.Range(0, 256 * 256).Select(i => (double)b.GetPixel(i % 256, i / 256).Red).ToList();
            double mean = values.Average();
            return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        }
        Assert.True(Std(clean) < Std(noisy) / 3, $"noise {Std(noisy):0.0} → {Std(clean):0.0}");
    }
}
