using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Jobs;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>
/// AI Denoise and AI Deblur in the editor. The "Denoise (AI)" and "Deblur (AI)" sliders blend the original with
/// AI-restored copies (see <see cref="RestorePipeline"/>). Missing copies are computed as urgent jobs in the
/// background queue and cached on disk; moving to another photo does not stop them, so the result is ready
/// when you come back.
/// </summary>
public partial class MainViewModel
{
    private readonly RestoreCache _restoreCache = new(RestoreCache.DefaultDirectory);
    private RestorePipeline? _pipeline;

    /// <summary>Used only from the queue's worker thread.</summary>
    private RestorePipeline Pipeline => _pipeline ??= new RestorePipeline(_models, _restoreCache);

    /// <summary>Restored copies of the open photo by variant ("denoise", "deblur", "deblur-of-denoised").</summary>
    private readonly Dictionary<string, SKBitmap> _restored = [];
    private readonly Dictionary<string, Task<SKBitmap?>> _restoring = [];
    private SKBitmap? _restoredFor;

    /// <summary>Preview of the unedited original (Before) and of the source the edit works on (AI blends).</summary>
    private PreviewImage? _originalPreview;
    private PreviewImage? _workingPreview;
    private SKBitmap? _working;
    private (double Denoise, double Deblur) _workingAmounts;

    private int _restoreGeneration;

    /// <summary>The open photo is waiting for AI denoise / deblur.</summary>
    [ObservableProperty]
    public partial bool IsRestoring { get; private set; }

    partial void OnShowOriginalChanged(bool value) => ShowPreview();

    private void ShowPreview()
    {
        var preview = ShowOriginal ? _originalPreview : _retouchedPreview ?? RetouchBase;
        if (preview is not null && !ReferenceEquals(Preview, preview))
            Preview = preview;
    }

    /// <summary>Called when a photo was shown: forget the previous photo's restored copies (its jobs keep running).</summary>
    private void ResetRestore(PreviewImage originalPreview)
    {
        _restored.Clear();
        _restoring.Clear();
        IsRestoring = false;
        _restoredFor = Original;
        _working = null;
        _workingAmounts = (0, 0);
        _originalPreview = originalPreview;
        _workingPreview = null;
        _retouchedPreview = null;
        _retouchedFrom = default;
        _lensPreview = null;
        _lensFull = null;
        _lensFrom = default;
        RefreshLens();
        UpdateLensPreview();
        UpdateRetouchedPreview();
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
            UpdateLensPreview();
            UpdateRetouchedPreview();
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
        UpdateLensPreview();
        UpdateRetouchedPreview();
        ShowPreview();
    }

    private (double Denoise, double Deblur) CurrentRestoreAmounts =>
        (State.Adjustments.DenoiseAmount / 100, State.Adjustments.DeblurAmount / 100);

    /// <summary>The source edits render from for the given amounts (0..1); null if a needed AI step failed or was cancelled.</summary>
    private async Task<SKBitmap?> WorkingImageAsync(SKBitmap original, (double Denoise, double Deblur) amounts)
    {
        SKBitmap? denoised = null;
        if (amounts.Denoise > 0 && (denoised = await RestoredAsync(original, RestoreKind.Denoise, original)) is null)
            return null;
        SKBitmap? deblurred = null;
        if (amounts.Deblur > 0 && (deblurred = await RestoredAsync(original, RestoreKind.Deblur, denoised ?? original)) is null)
            return null;
        return await Task.Run(() => RestorePipeline.Blend(original, denoised, amounts.Denoise, deblurred, amounts.Deblur));
    }

    /// <summary>
    /// A restored copy of <paramref name="source"/> (the original, or the denoised copy for deblur): from memory,
    /// or from an urgent job (disk cache or computed). Null if it failed, was cancelled, or another photo was opened.
    /// </summary>
    private Task<SKBitmap?> RestoredAsync(SKBitmap original, RestoreKind kind, SKBitmap source)
    {
        string variant = RestorePipeline.Variant(kind, !ReferenceEquals(source, original));
        if (!ReferenceEquals(_restoredFor, original) || FilePath is not { } path)
            return Task.FromResult<SKBitmap?>(null);
        if (_restored.TryGetValue(variant, out var done))
            return Task.FromResult<SKBitmap?>(done);
        if (_restoring.TryGetValue(variant, out var running))
            return running;
        string name = RestorePipeline.StepName(kind);
        var job = Enqueue(new BackgroundJob($"{name} – {Path.GetFileName(path)}",
            ctx => Pipeline.Restore(path, source, kind, variant,
                new Progress(p => ctx.Report(p.Fraction, p.Detail is null ? p.Step : $"{p.Step} · {p.Detail}")), ctx.Cancel),
            key: $"restore|{variant}|{path}", photoPath: path), urgent: true);
        var task = AwaitRestoreAsync(original, variant, name, job);
        _restoring[variant] = task;
        IsRestoring = true;
        return task;
    }

    private async Task<SKBitmap?> AwaitRestoreAsync(SKBitmap original, string variant, string name, BackgroundJob job)
    {
        try
        {
            var result = await job.Completion as SKBitmap;
            if (result is null || !ReferenceEquals(_restoredFor, original))
                return null; // another photo was opened meanwhile (the result is in the disk cache)
            _restored[variant] = result;
            Status = $"{name} done.";
            return result;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_restoredFor, original))
                Status = $"{name} cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_restoredFor, original))
                Status = $"{name} failed: {ex.Message}";
            return null;
        }
        finally
        {
            if (ReferenceEquals(_restoredFor, original))
            {
                _restoring.Remove(variant);
                IsRestoring = _restoring.Count > 0;
            }
        }
    }

    /// <summary>Stops the open photo's AI denoise / deblur (it starts again when a slider changes).</summary>
    [RelayCommand]
    private void CancelRestore()
    {
        if (FilePath is { } path)
            Queue.CancelAll(j => j.Key?.StartsWith("restore|", StringComparison.Ordinal) == true && j.PhotoPath == path);
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

    /// <summary>Reports on the calling thread (the job's worker), unlike <see cref="Progress{T}"/>.</summary>
    private sealed class Progress(Action<RestoreProgress> report) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => report(value);
    }
}
