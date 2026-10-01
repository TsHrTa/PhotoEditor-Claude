using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Library;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>
/// Opening photos. A RAW first shows a half-size render of itself (about half a second; the same development as the
/// full decode, so the tone does not change when the full photo replaces it; the camera's embedded JPEG, which has the
/// camera's own tone curve, is only the fallback) while the RAW decodes in the background; editing starts once the
/// decoded photo replaces it. The neighbours in the filmstrip
/// are decoded ahead, and the last few decoded photos are kept, so moving through a folder is quick.
/// </summary>
public partial class MainViewModel
{
    /// <summary>A decoded photo ready for editing.</summary>
    private sealed record DecodedPhoto(SKBitmap Bitmap, PreviewImage Preview, SKEncodedOrigin Orientation, TimeSpan DecodeTime);

    private readonly PrefetchingLoader<DecodedPhoto> _decoder = new(path =>
    {
        var watch = Stopwatch.StartNew();
        var bitmap = ImageLoader.Load(path);
        var photo = new DecodedPhoto(bitmap, PreviewImage.Create(bitmap), ImageLoader.ReadOrientation(path), watch.Elapsed);
        Timings.Log($"decoded {Path.GetFileName(path)} ({bitmap.Width} × {bitmap.Height}) in {watch.Elapsed.TotalSeconds:0.00} s");
        return photo;
    });

    private int _openGeneration;

    /// <summary>A photo is being opened (including the background decode of a RAW).</summary>
    [ObservableProperty]
    public partial bool IsOpening { get; private set; }

    /// <summary>Only the camera's preview is shown; the RAW is still decoding, so editing is not possible yet.</summary>
    [ObservableProperty]
    public partial bool IsShowingCameraPreview { get; private set; }

    /// <summary>
    /// Opens the photo at <paramref name="path"/>; reports failures in <see cref="Status"/>. When another photo
    /// is opened meanwhile this one is abandoned (returns false).
    /// </summary>
    public async Task<bool> OpenFileAsync(string path)
    {
        int generation = ++_openGeneration;
        bool Current() => generation == _openGeneration;
        var watch = Stopwatch.StartNew();
        SaveEdits(); // flush edits of the previous photo
        IsOpening = true;
        Status = $"Opening {Path.GetFileName(path)}…";
        SyncFilmstrip(path);
        try
        {
            bool ready = _decoder.TryGet(path, out _);
            if (!ready && RawImageLoader.IsRaw(path))
            {
                var quick = await Task.Run(() => CameraPreview(path));
                if (!Current())
                    return false;
                if (quick is not null)
                {
                    ShowCameraPreview(path, quick.Value.Preview, quick.Value.Geometry);
                    Timings.Log($"quick preview of {Path.GetFileName(path)} shown after {watch.Elapsed.TotalSeconds:0.00} s");
                }
            }

            var photo = await _decoder.Request(path, Neighbours(path));
            if (photo is null || !Current())
                return false;
            ShowDecoded(path, photo);
            string how = ready ? "already decoded ahead" : $"decoded in {photo.DecodeTime.TotalSeconds:0.0} s";
            Status += $" · ready after {watch.Elapsed.TotalSeconds:0.0} s ({how})";
            Timings.Log($"opened {Path.GetFileName(path)}: editable after {watch.Elapsed.TotalSeconds:0.00} s ({how})");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (Current())
            {
                Status = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
                IsShowingCameraPreview = false;
            }
            return false;
        }
        finally
        {
            if (Current())
                IsOpening = false;
        }
    }

    /// <summary>The photos to decode ahead: the next one in the filmstrip, then the previous one.</summary>
    private IReadOnlyList<string> Neighbours(string path)
    {
        int index = Photos.ToList().FindIndex(p => p.IsSamePath(path));
        if (index < 0)
            return [];
        return new[] { index + 1, index - 1 }.Where(i => i >= 0 && i < Photos.Count).Select(i => Photos[i].Path).ToList();
    }

    /// <summary>
    /// A quick preview of the RAW (<see cref="RawImageLoader.LoadQuick"/>: half size, the same rendering as the full
    /// decode; or, if LibRaw cannot read it, the camera's embedded JPEG) and the RAW's geometry in the preview's pixels;
    /// null if there is nothing usable. The viewer keeps its zoom when the decoded photo replaces it.
    /// </summary>
    private static (PreviewImage Preview, ImageGeometry Geometry)? CameraPreview(string path)
    {
        var bitmap = RawImageLoader.LoadQuick(path) ?? EmbeddedPreview.Load(path);
        if (bitmap is null)
            return null;
        ImageGeometry geometry;
        try
        {
            geometry = EditStore.ReadGeometry(path);
        }
        catch (InvalidDataException)
        {
            bitmap.Dispose();
            return null; // the decode will report the problem
        }
        return (PreviewImage.Create(bitmap, standIn: true), geometry with { Width = bitmap.Width, Height = bitmap.Height });
    }

    /// <summary>Shows the quick preview with the saved edit; the editing panels stay disabled until the RAW is decoded.</summary>
    private void ShowCameraPreview(string path, PreviewImage preview, ImageGeometry geometry)
    {
        SaveEdits();
        var (state, _) = LoadSidecar(path, geometry);
        Original = null;
        Preview = preview;
        SelectedMask = null;
        _history.Reset(state);
        State = state;
        _hasUnsavedEdits = false;
        UpdateHistoryCommands();
        ShowOriginal = false;
        FilePath = path;
        ResetRestore(preview);
        IsShowingCameraPreview = true;
        Status = $"{Path.GetFileName(path)}: preview — decoding the RAW for editing…";
    }

    /// <summary>Shows the decoded photo and makes it editable.</summary>
    private void ShowDecoded(string path, DecodedPhoto photo)
    {
        SaveEdits();
        var bitmap = photo.Bitmap;
        _geometry = new ImageGeometry(bitmap.Width, bitmap.Height, photo.Orientation, RawImageLoader.IsRaw(path));
        _lastXmpSkipped = "";
        var (state, sidecarNote) = LoadSidecar(path, _geometry);
        Original = bitmap;
        Preview = photo.Preview;
        SelectedMask = null;
        _history.Reset(state);
        State = state;
        _hasUnsavedEdits = false;
        UpdateHistoryCommands();
        ShowOriginal = false;
        FilePath = path;
        ResetRestore(photo.Preview);
        IsShowingCameraPreview = false;
        Status = $"{bitmap.Width} × {bitmap.Height}{sidecarNote}";
    }
}
