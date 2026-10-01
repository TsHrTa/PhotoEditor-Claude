using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>
/// Lens corrections: the shown photo corrected in the background (preview size first, then full size) whenever the
/// lens settings or the photo change; spot removal and AI masks then work on the corrected photo.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Lens profile corrections (distortion and vignetting from the lensfun database).</summary>
    public bool LensProfileEnabled
    {
        get => State.Adjustments.LensProfile;
        set
        {
            if (value != State.Adjustments.LensProfile)
                ApplyEdit(State with { Adjustments = State.Adjustments with { LensProfile = value } });
        }
    }

    /// <summary>Remove lateral chromatic aberration (from the profile, or measured in the photo).</summary>
    public bool RemoveChromaticAberration
    {
        get => State.Adjustments.RemoveChromaticAberration;
        set
        {
            if (value != State.Adjustments.RemoveChromaticAberration)
                ApplyEdit(State with { Adjustments = State.Adjustments with { RemoveChromaticAberration = value } });
        }
    }

    /// <summary>The open photo's camera / lens as the file names them.</summary>
    private PhotoLens PhotoLensInfo => PhotoLens.Of(FilePath);

    /// <summary>Which profile the open photo gets, or why none.</summary>
    public string LensProfileText
    {
        get
        {
            if (!HasImage)
                return "";
            if (!LensSetup.IsDatabaseDownloaded)
                return "Lens profiles are not downloaded yet.";
            var match = LensSetup.Match(PhotoLensInfo, LensSetup.Database);
            return match.Lens is null ? match.Describe() : $"Profile: {match.Describe()}";
        }
    }

    public bool IsLensDatabaseMissing => !LensSetup.IsDatabaseDownloaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadLensProfilesCommand))]
    public partial bool IsDownloadingLensProfiles { get; private set; }

    [RelayCommand(CanExecute = nameof(CanDownloadLensProfiles))]
    private async Task DownloadLensProfilesAsync()
    {
        IsDownloadingLensProfiles = true;
        try
        {
            var progress = new Progress<DownloadProgress>(p => Status = $"Downloading lens profiles… {p.Received} / {p.Total} files");
            int files = await LensSetup.DownloadDatabaseAsync(Http, progress);
            await Task.Run(() => _ = LensSetup.Database); // parse once, off the UI thread
            Status = files > 0
                ? $"Lens profiles downloaded ({LensSetup.Database.Lenses.Count} lenses)."
                : "Could not download the lens profiles (no internet connection?).";
        }
        finally
        {
            IsDownloadingLensProfiles = false;
        }
        RefreshLens();
        UpdateLensPreview();
    }

    private bool CanDownloadLensProfiles() => !IsDownloadingLensProfiles;

    private void RefreshLens()
    {
        OnPropertyChanged(nameof(LensProfileEnabled));
        OnPropertyChanged(nameof(RemoveChromaticAberration));
        OnPropertyChanged(nameof(LensProfileText));
        OnPropertyChanged(nameof(IsLensDatabaseMissing));
    }

    // ---- Corrected preview ----

    /// <summary>The shown photo with its lens corrections (null = none needed).</summary>
    private PreviewImage? _lensPreview;

    /// <summary>The corrected full-size photo (for AI masks and spot sources), once made.</summary>
    private SKBitmap? _lensFull;

    /// <summary>What <see cref="_lensPreview"/> was made from: the base and the lens settings.</summary>
    private (PreviewImage? Base, LensKey Key) _lensFrom;

    private bool _lensRunning;

    /// <summary>The lens settings the correction depends on (manual vignetting is done live by the renderer).</summary>
    private readonly record struct LensKey(bool Profile, bool Ca, double Distortion, string? Path, TransformKey Transform);

    /// <summary>The Transform panel's values (they move pixels, so they are part of the corrected photo).</summary>
    private readonly record struct TransformKey(double Vertical, double Horizontal, double Rotate, double Aspect, double Scale, double X, double Y)
    {
        public static TransformKey Of(AdjustmentSettings a) => new(a.TransformVertical, a.TransformHorizontal, a.TransformRotate,
            a.TransformAspect, a.TransformScale, a.TransformOffsetX, a.TransformOffsetY);
    }

    private LensKey CurrentLensKey => new(State.Adjustments.LensProfile, State.Adjustments.RemoveChromaticAberration,
        State.Adjustments.LensDistortion, FilePath, TransformKey.Of(State.Adjustments));

    /// <summary>The photo before spots: corrected, AI-restored or original.</summary>
    private PreviewImage? RetouchBase => _lensPreview ?? _workingPreview ?? _originalPreview;

    /// <summary>The full-size photo edits refer to (AI masks, spot sources): corrected when lens corrections are on.</summary>
    private SKBitmap? EditBase => _lensFull ?? _working ?? Original;

    /// <summary>Called when the lens settings, the photo or its AI-restored copy change.</summary>
    private void UpdateLensPreview()
    {
        var basePreview = _workingPreview ?? _originalPreview;
        var key = CurrentLensKey;
        if (basePreview is null)
            return;
        if (ReferenceEquals(_lensFrom.Base, basePreview) && _lensFrom.Key == key || _lensRunning)
            return;
        var settings = State.Adjustments;
        var lens = PhotoLensInfo;
        // (a quick check without measuring: measuring only matters once something is to be corrected)
        var full = LensSetup.For(lens, settings, basePreview.Width, basePreview.Height, measureCa: () => (1.001, 1));
        if (full is null)
        {
            _lensPreview = null;
            _lensFull = null;
            _lensFrom = (basePreview, key);
            UpdateRetouchedPreview();
            ShowPreview();
            return;
        }
        _lensRunning = true;
        _ = BuildLensPreviewAsync(basePreview, key, lens, settings);
    }

    private async Task BuildLensPreviewAsync(PreviewImage basePreview, LensKey key, PhotoLens lens, AdjustmentSettings settings)
    {
        bool Current() => ReferenceEquals(basePreview, _workingPreview ?? _originalPreview) && CurrentLensKey == key;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The chromatic aberration is measured once, on the full-size photo, as the export does (the measurement
            // uses a fixed-size copy, so the result is the same).
            Func<(double, double)> measure = () =>
            {
                using var bitmap = SKBitmap.FromImage(basePreview.Full);
                return ChromaticAberration.Measure(bitmap);
            };
            (double, double)? measured = null;
            Func<(double, double)> once = () => measured ??= measure();
            // 1. The preview size (what the fitted view shows); the full size follows.
            var small = await Task.Run(() => Corrected(basePreview.Preview, lens, settings, once));
            if (Current())
            {
                _lensPreview = PreviewImage.FromImages(ReferenceEquals(basePreview.Preview, basePreview.Full) ? small.Image : basePreview.Full, small.Image);
                _lensFull = null;
                UpdateRetouchedPreview();
                ShowPreview();
            }
            if (!ReferenceEquals(basePreview.Preview, basePreview.Full) && Current())
            {
                // 2. The full size (zoomed-in view, AI masks, spot sources).
                var large = await Task.Run(() => Corrected(basePreview.Full, lens, settings, once));
                if (Current())
                {
                    _lensPreview = PreviewImage.FromImages(large.Image, small.Image);
                    _lensFull = large.Bitmap;
                    UpdateRetouchedPreview();
                    ShowPreview();
                }
            }
            else if (Current())
                _lensFull = small.Bitmap;
            Timings.Log($"lens corrections in {watch.Elapsed.TotalMilliseconds:0} ms");
            _lensFrom = (basePreview, key);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Status = $"Lens corrections failed: {ex.Message}";
            _lensFrom = (basePreview, key);
        }
        finally
        {
            _lensRunning = false;
        }
        if (!ReferenceEquals(_lensFrom.Base, _workingPreview ?? _originalPreview) || _lensFrom.Key != CurrentLensKey)
            UpdateLensPreview();
    }

    /// <summary>One image of the shown photo corrected; its headroom and vignetting table attached, maps shared.</summary>
    private static (SKImage Image, SKBitmap? Bitmap) Corrected(SKImage image, PhotoLens lens, AdjustmentSettings settings,
        Func<(double, double)> measureCa)
    {
        var correction = LensSetup.For(lens, settings, image.Width, image.Height, measureCa: measureCa)
            ?? new LensCorrection(image.Width, image.Height, null, 1); // nothing measured after all
        if (correction.IsGeometryIdentity)
        {
            // Only vignetting: the same pixels under a new image object that carries the table.
            using var pixmap = image.PeekPixels();
            var same = SKImage.FromPixels(pixmap, (_, context) => GC.KeepAlive(context), image)
                ?? throw new InvalidOperationException("Could not wrap the photo.");
            Headroom.Attach(same, Headroom.Of(image));
            LensVignetting.Attach(same, correction.Shading);
            ToneBaseMap.Share(image, same);
            HazeMap.Share(image, same);
            return (same, null);
        }
        using var bitmap = SKBitmap.FromImage(image);
        Headroom.Attach(bitmap, Headroom.Of(image));
        var corrected = correction.Apply(bitmap);
        corrected.SetImmutable();
        var result = SKImage.FromBitmap(corrected);
        Headroom.Attach(result, Headroom.Of(corrected));
        LensVignetting.Attach(result, correction.Shading);
        return (result, corrected);
    }
}
