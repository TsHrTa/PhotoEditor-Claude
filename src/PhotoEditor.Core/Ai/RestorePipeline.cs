using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>A step of the AI restore with its progress, for status displays.</summary>
public readonly record struct RestoreProgress(string Step, double? Fraction, string? Detail = null);

/// <summary>
/// AI Denoise and AI Deblur of a photo as the editor and export use them: restored copies come from the disk
/// cache or are computed (models downloaded on first use) and cached; the working image is
/// blend(blend(original, denoised, denoise), deblurred, deblur), where the deblurred copy is made from the fully
/// denoised one when Denoise is on (deblurring raw noise would amplify it). Not thread-safe: use it from one
/// thread at a time (the job queue's worker).
/// </summary>
public sealed class RestorePipeline(ModelStore models, RestoreCache cache) : IDisposable
{
    private readonly Dictionary<RestoreKind, ImageRestorer> _restorers = [];

    public static string StepName(RestoreKind kind) => kind == RestoreKind.Denoise ? "AI Denoise" : "AI Deblur";

    /// <summary>The cache variant of a restored copy ("denoise", "deblur", "deblur-of-denoised").</summary>
    public static string Variant(RestoreKind kind, bool fromDenoised) =>
        kind == RestoreKind.Denoise ? "denoise" : fromDenoised ? "deblur-of-denoised" : "deblur";

    /// <summary>The cached copy if there is one (null otherwise).</summary>
    public SKBitmap? Cached(string? imagePath, SKBitmap source, string variant) =>
        imagePath is null ? null : cache.Load(imagePath, variant, source.Width, source.Height);

    /// <summary>
    /// The restored copy of <paramref name="source"/>: from the cache, or computed now and cached
    /// (<paramref name="imagePath"/> null = don't cache).
    /// </summary>
    public SKBitmap Restore(string? imagePath, SKBitmap source, RestoreKind kind, string variant,
        IProgress<RestoreProgress>? progress, CancellationToken cancel)
    {
        if (Cached(imagePath, source, variant) is { } cached)
            return cached;
        string step = StepName(kind);
        var files = ImageRestorer.ModelFiles(kind);
        if (!files.All(models.IsAvailable))
        {
            long total = files.Where(m => !models.IsAvailable(m)).Sum(m => m.SizeBytes);
            var download = new SyncProgress<DownloadProgress>(p =>
                progress?.Report(new RestoreProgress($"Downloading the {step} model", p.Fraction, $"{total / 1_000_000} MB")));
            models.GetAllAsync(files, download, cancel).GetAwaiter().GetResult();
        }
        if (!_restorers.TryGetValue(kind, out var restorer))
            _restorers[kind] = restorer = ImageRestorer.Load(models, kind);
        string device = restorer.Device == InferenceDevice.DirectML ? "on the GPU" : "on the CPU";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tiles = new SyncProgress<TileProgress>(p =>
            progress?.Report(new RestoreProgress(device, p.Fraction,
                p.Done == 0 ? null : $"about {Remaining(watch.Elapsed, p)} left")));
        var result = restorer.Restore(source, tiles, cancel);
        if (imagePath is not null)
        {
            try
            {
                cache.Save(imagePath, variant, result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not fatal: it is recomputed next time.
            }
        }
        return result;
    }

    /// <summary>
    /// The image edits render from for the given amounts (0..1). Returns <paramref name="original"/> itself when
    /// both are 0, otherwise a new bitmap.
    /// </summary>
    public SKBitmap Working(string? imagePath, SKBitmap original, double denoise, double deblur,
        IProgress<RestoreProgress>? progress, CancellationToken cancel)
    {
        if (denoise <= 0 && deblur <= 0)
            return original;
        using var denoised = denoise > 0
            ? Restore(imagePath, original, RestoreKind.Denoise, Variant(RestoreKind.Denoise, false), progress, cancel)
            : null;
        using var deblurred = deblur > 0
            ? Restore(imagePath, denoised ?? original, RestoreKind.Deblur, Variant(RestoreKind.Deblur, denoised is not null), progress, cancel)
            : null;
        return Blend(original, denoised, denoise, deblurred, deblur);
    }

    /// <summary>blend(blend(original, denoised, denoise), deblurred, deblur) as a new bitmap.</summary>
    public static SKBitmap Blend(SKBitmap original, SKBitmap? denoised, double denoise, SKBitmap? deblurred, double deblur)
    {
        var result = denoised is null ? original : ImageRestorer.Blend(original, denoised, denoise);
        if (deblurred is not null)
        {
            var blended = ImageRestorer.Blend(result, deblurred, deblur);
            if (!ReferenceEquals(result, original))
                result.Dispose();
            result = blended;
        }
        return ReferenceEquals(result, original) ? original.Copy() : result;
    }

    private static string Remaining(TimeSpan elapsed, TileProgress p)
    {
        var left = TimeSpan.FromTicks(elapsed.Ticks * (p.Total - p.Done) / Math.Max(1, p.Done));
        return left.TotalMinutes >= 1 ? $"{left.TotalMinutes:0} min" : $"{left.TotalSeconds:0} s";
    }

    public void Dispose()
    {
        foreach (var restorer in _restorers.Values)
            restorer.Dispose();
        _restorers.Clear();
    }

    /// <summary>Reports on the calling thread (unlike <see cref="Progress{T}"/>, which posts to a sync context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
