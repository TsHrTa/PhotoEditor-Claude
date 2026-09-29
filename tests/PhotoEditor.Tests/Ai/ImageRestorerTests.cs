using PhotoEditor.Core.Ai;
using SkiaSharp;

namespace PhotoEditor.Tests.Ai;

public sealed class ImageRestorerTests : IDisposable
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
        using var result = ImageRestorer.ProcessTiles(img, t =>
        {
            calls++;
            Assert.Equal(0, t.Shape[2] % 8);
            Assert.Equal(0, t.Shape[3] % 8);
            return t;
        });
        int diff = TestImages.MaxDifference(img, result, out var at);
        Assert.True(diff <= 1, $"difference {diff} at {at}"); // weighted average of equal values, rounding only
        Assert.True(calls >= 1);
    }

    [Fact]
    public void ProcessTiles_CrossFadesTilesThatDriftInBrightness()
    {
        // A flat grey image and a "model" that brightens each window by a different amount (like SCUNet's
        // slight per-window drift): with cross-fading there is no visible step between neighbouring pixels.
        using var flat = new SKBitmap(new SKImageInfo(1200, 800, SKColorType.Rgba8888, SKAlphaType.Premul));
        flat.Erase(new SKColor(40, 40, 40));
        int call = 0;
        using var result = ImageRestorer.ProcessTiles(flat, t =>
        {
            float offset = (call++ % 3) * 0.03f; // up to ~8 levels between windows
            return t with { Data = t.Data.Select(v => v + offset).ToArray() };
        });
        int maxStep = 0;
        for (int y = 0; y < 800; y += 7)
        for (int x = 1; x < 1200; x++)
            maxStep = Math.Max(maxStep, Math.Abs(result.GetPixel(x, y).Red - result.GetPixel(x - 1, y).Red));
        for (int x = 0; x < 1200; x += 7)
        for (int y = 1; y < 800; y++)
            maxStep = Math.Max(maxStep, Math.Abs(result.GetPixel(x, y).Red - result.GetPixel(x, y - 1).Red));
        Assert.True(maxStep <= 1, $"largest step between neighbouring pixels: {maxStep}");
    }

    [Fact]
    public void ProcessTiles_PadsWindowsToTheModelsShape()
    {
        // NAFNet rejects inputs under 384 px, SCUNet sizes that are not multiples of 64: a thin strip is mirror-padded and cropped back unchanged.
        using var strip = Noisy(600, 100);
        var shapes = new List<long[]>();
        using var result = ImageRestorer.ProcessTiles(strip, t => { shapes.Add(t.Shape.ToArray()); return t; },
            shape: new WindowShape(Multiple: 64, Minimum: 384));
        // SCUNet needs multiples of 64: the 600 px side becomes one full 512 window plus another, the 100 px side 384.
        Assert.All(shapes, s => Assert.True(s[2] >= 384 && s[3] >= 384 && s[2] % 64 == 0 && s[3] % 64 == 0, $"window {s[2]} × {s[3]}"));
        Assert.Equal((600, 100), (result.Width, result.Height));
        Assert.True(TestImages.MaxDifference(strip, result, out _) <= 1);
    }

    [Fact]
    public void ProcessTiles_ReportsProgress_AndCanBeCancelled()
    {
        using var img = Noisy(1000, 500);
        var reports = new List<TileProgress>();
        using (ImageRestorer.ProcessTiles(img, t => t, new SyncProgress(reports.Add))) { }
        Assert.Equal(reports.Count, reports[^1].Total);
        Assert.Equal(1.0, reports[^1].Fraction);

        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            ImageRestorer.ProcessTiles(img, t => { cts.Cancel(); return t; }, null, cts.Token));
    }

    [Fact]
    public void Blend_InterpolatesPixels()
    {
        using var a = TestImages.Solid(new SKColor(0, 100, 200));
        using var b = TestImages.Solid(new SKColor(200, 100, 0));
        using var half = ImageRestorer.Blend(a, b, 0.5);
        Assert.Equal(new SKColor(100, 100, 100), half.GetPixel(1, 1));
        using var none = ImageRestorer.Blend(a, b, 0);
        Assert.Equal(0, TestImages.MaxDifference(a, none, out _));
    }

    [Fact]
    public void Cache_RoundTripsAndChangesWithThePhoto()
    {
        var photo = Path.Combine(_dir, "p.jpg");
        File.WriteAllText(photo, "v1");
        var cache = new RestoreCache(Path.Combine(_dir, "cache"));
        using var img = Noisy(20, 10);
        Assert.Null(cache.Load(photo, "denoise", 20, 10));
        cache.Save(photo, "denoise", img);
        Assert.Null(cache.Load(photo, "deblur", 20, 10)); // other variant
        using (var loaded = cache.Load(photo, "denoise", 20, 10))
            Assert.Equal(0, TestImages.MaxDifference(img, loaded!, out _));
        Assert.Null(cache.Load(photo, "denoise", 21, 10)); // size mismatch → recompute

        var before = cache.PathFor(photo, "denoise");
        File.WriteAllText(photo, "v2 (edited elsewhere)");
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(before, cache.PathFor(photo, "denoise"));
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
        using var denoiser = ImageRestorer.Load(store, RestoreKind.Denoise, InferenceDevice.Cpu);
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
        using var clean = denoiser.Restore(noisy);
        static double Std(SKBitmap b)
        {
            var values = Enumerable.Range(0, 256 * 256).Select(i => (double)b.GetPixel(i % 256, i / 256).Red).ToList();
            double mean = values.Average();
            return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        }
        Assert.True(Std(clean) < Std(noisy) / 3, $"noise {Std(noisy):0.0} → {Std(clean):0.0}");
    }
    /// <summary>Runs NAFNet when PHOTOEDITOR_MODELS contains it (otherwise does nothing).</summary>
    [Fact]
    public void RealModel_SharpensMotionBlur()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOEDITOR_MODELS");
        if (dir is null)
            return;
        var store = new ModelStore(dir, new HttpClient());
        if (!ModelCatalog.Deblur.All(store.IsAvailable))
            return;
        using var deblurrer = ImageRestorer.Load(store, RestoreKind.Deblur, InferenceDevice.Cpu);
        // Vertical bars 24 px wide, smeared by a 9 px horizontal motion blur: edges should get steeper.
        const int size = 192, blur = 9;
        using var blurred = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double sum = 0;
            for (int k = -blur / 2; k <= blur / 2; k++)
                sum += ((x + k + size) / 24 % 2 == 0) ? 40 : 210;
            byte v = (byte)Math.Round(sum / blur);
            blurred.SetPixel(x, y, new SKColor(v, v, v));
        }
        using var sharp = deblurrer.Restore(blurred);
        static double MaxStep(SKBitmap b)
        {
            double best = 0;
            for (int y = 32; y < size - 32; y += 8)
            for (int x = 32; x < size - 32; x++)
                best = Math.Max(best, Math.Abs(b.GetPixel(x + 1, y).Red - b.GetPixel(x, y).Red));
            return best;
        }
        Assert.True(MaxStep(sharp) > MaxStep(blurred) * 1.5, $"edge step {MaxStep(blurred):0} → {MaxStep(sharp):0}");
    }
}
