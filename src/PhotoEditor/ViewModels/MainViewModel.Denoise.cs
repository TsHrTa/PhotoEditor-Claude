using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>
/// AI Denoise: the "Denoise (AI)" slider blends the original with an AI-denoised copy. The copy is computed
/// once per photo (tiled, with progress and Cancel), cached on disk, and used for the preview and export.
/// </summary>
public partial class MainViewModel
{
    private readonly DenoiseCache _denoiseCache = new(DenoiseCache.DefaultDirectory);
    private Denoiser? _denoiser;

    /// <summary>The denoised copy of <see cref="Original"/> (null until computed / loaded).</summary>
    private SKBitmap? _denoised;
    private SKBitmap? _denoisedFor;

    /// <summary>Preview of the unedited original (Before) and of the source the edit works on (denoised blend).</summary>
    private PreviewImage? _originalPreview;
    private PreviewImage? _workingPreview;
    private SKBitmap? _working;
    private double _workingAmount;

    private CancellationTokenSource? _denoiseCancel;
    private Task<SKBitmap?>? _denoiseTask;
    private int _denoiseGeneration;

    [ObservableProperty]
    public partial bool IsDenoising { get; private set; }

    /// <summary>The bitmap edits are rendered from: the original, or its blend with the denoised copy.</summary>
    private SKBitmap? WorkingImage => _working ?? Original;

    partial void OnShowOriginalChanged(bool value) => ShowPreview();

    private void ShowPreview()
    {
        var preview = ShowOriginal ? _originalPreview : _workingPreview ?? _originalPreview;
        if (preview is not null && !ReferenceEquals(Preview, preview))
            Preview = preview;
    }

    /// <summary>Called when a photo was opened: forget the previous photo's denoise state.</summary>
    private void ResetDenoise(PreviewImage originalPreview)
    {
        _denoiseCancel?.Cancel();
        _denoiseTask = null;
        _denoised = null;
        _denoisedFor = null;
        _working = null;
        _workingAmount = 0;
        _originalPreview = originalPreview;
        _workingPreview = null;
        ScheduleDenoiseUpdate();
    }

    /// <summary>Updates the working image shortly after the Denoise slider moved (a drag = one update).</summary>
    private void ScheduleDenoiseUpdate()
    {
        int generation = ++_denoiseGeneration;
        _ = UpdateDenoiseAfterDelayAsync(generation);
    }

    private async Task UpdateDenoiseAfterDelayAsync(int generation)
    {
        await Task.Delay(150);
        if (generation != _denoiseGeneration || Original is not { } original)
            return;
        double amount = State.Adjustments.DenoiseAmount / 100;
        if (amount <= 0)
        {
            _working = null;
            _workingAmount = 0;
            _workingPreview = null;
            ShowPreview();
            return;
        }
        var denoised = await EnsureDenoisedAsync(original);
        if (denoised is null || generation != _denoiseGeneration || !ReferenceEquals(Original, original))
            return;
        amount = State.Adjustments.DenoiseAmount / 100;
        if (_working is not null && Math.Abs(amount - _workingAmount) < 1e-6)
            return;
        var (blended, preview) = await Task.Run(() =>
        {
            var b = Denoiser.Blend(original, denoised, amount);
            return (b, PreviewImage.Create(b));
        });
        if (!ReferenceEquals(Original, original))
            return;
        _working = blended;
        _workingAmount = amount;
        _workingPreview = preview;
        ShowPreview();
    }

    /// <summary>The denoised copy of <paramref name="original"/>: from memory, the disk cache, or computed now.</summary>
    private Task<SKBitmap?> EnsureDenoisedAsync(SKBitmap original)
    {
        if (_denoised is not null && ReferenceEquals(_denoisedFor, original))
            return Task.FromResult<SKBitmap?>(_denoised);
        if (_denoiseTask is not null && ReferenceEquals(_denoisedFor, original))
            return _denoiseTask;
        _denoisedFor = original;
        _denoiseCancel = new CancellationTokenSource();
        return _denoiseTask = ComputeDenoisedAsync(original, FilePath, _denoiseCancel.Token);
    }

    private async Task<SKBitmap?> ComputeDenoisedAsync(SKBitmap original, string? path, CancellationToken cancel)
    {
        IsDenoising = true;
        try
        {
            if (path is not null && await Task.Run(() => _denoiseCache.Load(path, original.Width, original.Height)) is { } cached)
            {
                Status = "AI Denoise: loaded the saved result.";
                return Keep(original, cached);
            }

            if (!ModelCatalog.Denoise.All(_models.IsAvailable))
            {
                long total = ModelCatalog.Denoise.Where(m => !_models.IsAvailable(m)).Sum(m => m.SizeBytes);
                var download = new Progress<DownloadProgress>(p =>
                    Status = $"Downloading the AI Denoise model… {p.Fraction ?? 0:P0} of {total / 1_000_000} MB");
                await _models.GetAllAsync(ModelCatalog.Denoise, download, cancel);
            }
            _denoiser ??= await Task.Run(() => Denoiser.Load(_models), cancel);
            string device = _denoiser.Device == InferenceDevice.DirectML ? "GPU" : "CPU — slow";
            var watch = Stopwatch.StartNew();
            var progress = new Progress<TileProgress>(p =>
            {
                string eta = p.Done == 0 ? "" : $", about {Remaining(watch.Elapsed, p)} left";
                Status = $"AI Denoise ({device}): {p.Fraction:P0}{eta}. Keeps running while you edit; Cancel stops it.";
            });
            var denoiser = _denoiser;
            var result = await Task.Run(() => denoiser.Denoise(original, progress, cancel), cancel);
            if (path is not null)
            {
                try
                {
                    await Task.Run(() => _denoiseCache.Save(path, result));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not fatal: it is recomputed next time.
                }
            }
            Status = $"AI Denoise done ({watch.Elapsed.TotalSeconds:0} s).";
            return Keep(original, result);
        }
        catch (OperationCanceledException)
        {
            Status = "AI Denoise cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or UnauthorizedAccessException)
        {
            Status = $"AI Denoise failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsDenoising = false;
            if (ReferenceEquals(_denoisedFor, original))
                _denoiseTask = null;
        }
    }

    private SKBitmap? Keep(SKBitmap original, SKBitmap denoised)
    {
        if (!ReferenceEquals(Original, original))
            return null; // another photo was opened meanwhile
        _denoised = denoised;
        return denoised;
    }

    private static string Remaining(TimeSpan elapsed, TileProgress p)
    {
        var left = TimeSpan.FromTicks(elapsed.Ticks * (p.Total - p.Done) / Math.Max(1, p.Done));
        return left.TotalMinutes >= 1 ? $"{left.TotalMinutes:0} min" : $"{left.TotalSeconds:0} s";
    }

    [RelayCommand]
    private void CancelDenoise() => _denoiseCancel?.Cancel();

    /// <summary>The source to export from: the working image, waiting for the denoised copy if it is still being computed.</summary>
    private async Task<SKBitmap?> ExportSourceAsync(SKBitmap original, double amount)
    {
        if (amount <= 0)
            return original;
        if (_working is not null && Math.Abs(_workingAmount - amount / 100) < 1e-6)
            return _working;
        var denoised = await EnsureDenoisedAsync(original);
        return denoised is null ? null : await Task.Run(() => Denoiser.Blend(original, denoised, amount / 100));
    }
}
