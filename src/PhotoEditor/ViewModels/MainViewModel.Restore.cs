using System;
using System.Collections.Generic;
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
/// AI Denoise and AI Deblur. The "Denoise (AI)" and "Deblur (AI)" sliders blend the original with AI-restored
/// copies, which are computed once per photo (tiled, with progress and Cancel), cached on disk and used for the
/// preview and export: working = blend(blend(original, denoised, denoise), deblurred, deblur), where the deblurred
/// copy is made from the denoised one when Denoise is on (deblurring raw noise would amplify it).
/// </summary>
public partial class MainViewModel
{
    private readonly RestoreCache _restoreCache = new(RestoreCache.DefaultDirectory);
    private readonly Dictionary<RestoreKind, ImageRestorer> _restorers = [];

    /// <summary>Restored copies of the open photo by variant ("denoise", "deblur", "deblur-of-denoised").</summary>
    private readonly Dictionary<string, SKBitmap> _restored = [];
    private readonly Dictionary<string, Task<SKBitmap?>> _restoring = [];
    private SKBitmap? _restoredFor;

    /// <summary>Preview of the unedited original (Before) and of the source the edit works on (AI blends).</summary>
    private PreviewImage? _originalPreview;
    private PreviewImage? _workingPreview;
    private SKBitmap? _working;
    private (double Denoise, double Deblur) _workingAmounts;

    private CancellationTokenSource _restoreCancel = new();
    private int _restoreGeneration;

    [ObservableProperty]
    public partial bool IsRestoring { get; private set; }

    partial void OnShowOriginalChanged(bool value) => ShowPreview();

    private void ShowPreview()
    {
        var preview = ShowOriginal ? _originalPreview : _workingPreview ?? _originalPreview;
        if (preview is not null && !ReferenceEquals(Preview, preview))
            Preview = preview;
    }

    /// <summary>Called when a photo was opened: forget the previous photo's restored copies.</summary>
    private void ResetRestore(PreviewImage originalPreview)
    {
        _restoreCancel.Cancel();
        _restoreCancel = new CancellationTokenSource();
        _restored.Clear();
        _restoring.Clear();
        _restoredFor = Original;
        _working = null;
        _workingAmounts = (0, 0);
        _originalPreview = originalPreview;
        _workingPreview = null;
        ScheduleRestoreUpdate();
    }

    /// <summary>Updates the working image shortly after a Denoise / Deblur slider moved (a drag = one update).</summary>
    private void ScheduleRestoreUpdate()
    {
        int generation = ++_restoreGeneration;
        _ = UpdateRestoreAfterDelayAsync(generation);
    }

    private async Task UpdateRestoreAfterDelayAsync(int generation)
    {
        await Task.Delay(150);
        if (generation != _restoreGeneration || Original is not { } original)
            return;
        var amounts = CurrentRestoreAmounts;
        if (amounts == (0, 0))
        {
            _working = null;
            _workingAmounts = (0, 0);
            _workingPreview = null;
            ShowPreview();
            return;
        }
        if (_working is not null && _workingAmounts == amounts)
            return;
        var working = await WorkingImageAsync(original, amounts);
        if (working is null || generation != _restoreGeneration || !ReferenceEquals(Original, original))
            return;
        var preview = await Task.Run(() => PreviewImage.Create(working));
        if (generation != _restoreGeneration || !ReferenceEquals(Original, original))
            return;
        _working = working;
        _workingAmounts = amounts;
        _workingPreview = preview;
        ShowPreview();
    }

    private (double Denoise, double Deblur) CurrentRestoreAmounts =>
        (State.Adjustments.DenoiseAmount / 100, State.Adjustments.DeblurAmount / 100);

    /// <summary>The source edits render from for the given amounts (0..1); null if a needed AI step failed or was cancelled.</summary>
    private async Task<SKBitmap?> WorkingImageAsync(SKBitmap original, (double Denoise, double Deblur) amounts)
    {
        SKBitmap? denoised = null;
        if (amounts.Denoise > 0 && (denoised = await RestoredAsync(original, RestoreKind.Denoise, original, "denoise")) is null)
            return null;
        SKBitmap? deblurred = null;
        if (amounts.Deblur > 0)
        {
            var from = denoised ?? original;
            if ((deblurred = await RestoredAsync(original, RestoreKind.Deblur, from, denoised is null ? "deblur" : "deblur-of-denoised")) is null)
                return null;
        }
        return await Task.Run(() =>
        {
            var result = denoised is null ? original : ImageRestorer.Blend(original, denoised, amounts.Denoise);
            if (deblurred is not null)
            {
                var blended = ImageRestorer.Blend(result, deblurred, amounts.Deblur);
                if (!ReferenceEquals(result, original))
                    result.Dispose();
                result = blended;
            }
            return result;
        });
    }

    /// <summary>A restored copy of <paramref name="source"/>: from memory, the disk cache, or computed now (once per variant).</summary>
    private Task<SKBitmap?> RestoredAsync(SKBitmap original, RestoreKind kind, SKBitmap source, string variant)
    {
        if (!ReferenceEquals(_restoredFor, original))
            return Task.FromResult<SKBitmap?>(null);
        if (_restored.TryGetValue(variant, out var done))
            return Task.FromResult<SKBitmap?>(done);
        if (_restoring.TryGetValue(variant, out var running))
            return running;
        var task = ComputeRestoredAsync(original, kind, source, variant, FilePath, _restoreCancel.Token);
        _restoring[variant] = task;
        return task;
    }

    private async Task<SKBitmap?> ComputeRestoredAsync(SKBitmap original, RestoreKind kind, SKBitmap source, string variant,
        string? path, CancellationToken cancel)
    {
        string name = kind == RestoreKind.Denoise ? "AI Denoise" : "AI Deblur";
        IsRestoring = true;
        try
        {
            if (path is not null && await Task.Run(() => _restoreCache.Load(path, variant, source.Width, source.Height)) is { } cached)
            {
                Status = $"{name}: loaded the saved result.";
                return Keep(original, variant, cached);
            }

            var files = ImageRestorer.ModelFiles(kind);
            if (!files.All(_models.IsAvailable))
            {
                long total = files.Where(m => !_models.IsAvailable(m)).Sum(m => m.SizeBytes);
                var download = new Progress<DownloadProgress>(p =>
                    Status = $"Downloading the {name} model… {p.Fraction ?? 0:P0} of {total / 1_000_000} MB");
                await _models.GetAllAsync(files, download, cancel);
            }
            if (!_restorers.TryGetValue(kind, out var restorer))
                _restorers[kind] = restorer = await Task.Run(() => ImageRestorer.Load(_models, kind), cancel);
            string device = restorer.Device == InferenceDevice.DirectML ? "GPU" : "CPU — slow";
            var watch = Stopwatch.StartNew();
            var progress = new Progress<TileProgress>(p =>
            {
                string eta = p.Done == 0 ? "" : $", about {Remaining(watch.Elapsed, p)} left";
                Status = $"{name} ({device}): {p.Fraction:P0}{eta}. Keeps running while you edit; Cancel stops it.";
            });
            var result = await Task.Run(() => restorer.Restore(source, progress, cancel), cancel);
            if (path is not null)
            {
                try
                {
                    await Task.Run(() => _restoreCache.Save(path, variant, result));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not fatal: it is recomputed next time.
                }
            }
            Status = $"{name} done ({watch.Elapsed.TotalSeconds:0} s).";
            return Keep(original, variant, result);
        }
        catch (OperationCanceledException)
        {
            Status = $"{name} cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
            or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or UnauthorizedAccessException)
        {
            Status = $"{name} failed: {ex.Message}";
            return null;
        }
        finally
        {
            if (ReferenceEquals(_restoredFor, original))
                _restoring.Remove(variant);
            IsRestoring = _restoring.Count > 0;
        }
    }

    private SKBitmap? Keep(SKBitmap original, string variant, SKBitmap restored)
    {
        if (!ReferenceEquals(_restoredFor, original))
            return null; // another photo was opened meanwhile
        _restored[variant] = restored;
        return restored;
    }

    private static string Remaining(TimeSpan elapsed, TileProgress p)
    {
        var left = TimeSpan.FromTicks(elapsed.Ticks * (p.Total - p.Done) / Math.Max(1, p.Done));
        return left.TotalMinutes >= 1 ? $"{left.TotalMinutes:0} min" : $"{left.TotalSeconds:0} s";
    }

    /// <summary>Stops the running AI denoise / deblur (it starts again when a slider changes).</summary>
    [RelayCommand]
    private void CancelRestore()
    {
        _restoreCancel.Cancel();
        _restoreCancel = new CancellationTokenSource();
    }

    /// <summary>The source to export from, waiting for the AI copies if they are still being computed.</summary>
    private async Task<SKBitmap?> ExportSourceAsync(SKBitmap original, (double Denoise, double Deblur) amounts)
    {
        if (amounts == (0, 0))
            return original;
        if (_working is not null && _workingAmounts == amounts)
            return _working;
        return await WorkingImageAsync(original, amounts);
    }
}
