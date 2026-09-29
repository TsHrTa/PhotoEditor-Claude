using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Library;

namespace PhotoEditor.ViewModels;

/// <summary>
/// The folder of the open photo, shown as a filmstrip: thumbnails load in the background (nearest to the open
/// photo first, from the embedded RAW previews, cached on disk) and show the saved edits; selecting one opens it.
/// </summary>
public partial class MainViewModel
{
    private readonly ThumbnailCache _thumbnails = new(ThumbnailCache.DefaultDirectory);
    private CancellationTokenSource _thumbnailCancel = new();

    /// <summary>Set while the filmstrip selection follows an opened photo (so it doesn't open it again).</summary>
    private bool _syncingFilmstrip;

    public ObservableCollection<FilmstripItem> Photos { get; } = [];

    [ObservableProperty]
    public partial string? FolderPath { get; private set; }

    [ObservableProperty]
    public partial bool HasFolder { get; private set; }

    /// <summary>The filmstrip selection; choosing another photo opens it.</summary>
    [ObservableProperty]
    public partial FilmstripItem? CurrentPhoto { get; set; }

    partial void OnCurrentPhotoChanged(FilmstripItem? value)
    {
        NextPhotoCommand.NotifyCanExecuteChanged();
        PreviousPhotoCommand.NotifyCanExecuteChanged();
        if (value is not null && !_syncingFilmstrip && !value.IsSamePath(FilePath))
            _ = OpenFileAsync(value.Path);
    }

    /// <summary>Shows the folder's photos and opens the first one.</summary>
    public async Task OpenFolderAsync(string folder)
    {
        if (!ShowFolder(folder, null))
            return;
        if (Photos.Count == 0)
        {
            Status = $"No photos in {folder}";
            return;
        }
        await OpenFileAsync(Photos[0].Path);
    }

    /// <summary>
    /// Fills the filmstrip with <paramref name="folder"/> (unless it is already shown) and starts loading its
    /// thumbnails around <paramref name="current"/>; false if the folder can't be read.
    /// </summary>
    private bool ShowFolder(string folder, string? current)
    {
        if (FolderPath is not null && string.Equals(Path.GetFullPath(FolderPath), Path.GetFullPath(folder),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return true;
        IReadOnlyList<string> files;
        try
        {
            files = PhotoFolder.List(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not read the folder: {ex.Message}";
            return false;
        }
        _thumbnailCancel.Cancel();
        _thumbnailCancel = new CancellationTokenSource();
        _syncingFilmstrip = true;
        Photos.Clear();
        foreach (var file in files)
            Photos.Add(new FilmstripItem(file) { HasEdits = PhotoFolder.HasEdits(file) });
        CurrentPhoto = null;
        _syncingFilmstrip = false;
        FolderPath = folder;
        HasFolder = Photos.Count > 0;
        int start = Math.Max(0, Photos.ToList().FindIndex(p => p.IsSamePath(current)));
        _ = LoadThumbnailsAsync(start, _thumbnailCancel.Token);
        return true;
    }

    /// <summary>After a photo opened: show its folder (loading the thumbnails around it) and select it.</summary>
    private void SyncFilmstrip(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { } folder)
            return;
        if (!ShowFolder(folder, path))
            return;
        _syncingFilmstrip = true;
        CurrentPhoto = Photos.FirstOrDefault(p => p.IsSamePath(path));
        _syncingFilmstrip = false;
    }

    /// <summary>Loads every thumbnail, nearest to <paramref name="start"/> first, two at a time.</summary>
    private async Task LoadThumbnailsAsync(int start, CancellationToken cancel)
    {
        var items = Photos.ToList();
        await Task.Yield();
        start = Math.Clamp(start, 0, Math.Max(0, items.Count - 1));
        var order = items.Select((item, i) => (item, i)).OrderBy(x => Math.Abs(x.i - start)).Select(x => x.item).ToList();
        try
        {
            await Parallel.ForEachAsync(order, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancel },
                (item, _) =>
                {
                    LoadThumbnail(item);
                    return ValueTask.CompletedTask;
                });
        }
        catch (OperationCanceledException)
        {
            // Another folder was opened.
        }
    }

    /// <summary>Makes the item's thumbnail (with its saved edit) on the calling thread and shows it on the UI thread.</summary>
    private void LoadThumbnail(FilmstripItem item)
    {
        using var thumbnail = PhotoFolder.EditedThumbnail(_thumbnails, item.Path);
        if (thumbnail is null)
            return;
        var bitmap = FilmstripItem.ToAvalonia(thumbnail);
        bool hasEdits = PhotoFolder.HasEdits(item.Path);
        Dispatcher.UIThread.Post(() =>
        {
            var old = item.Thumbnail;
            item.Thumbnail = bitmap;
            item.HasEdits = hasEdits;
            old?.Dispose();
        });
    }

    /// <summary>The open photo's edit was saved: update its thumbnail and edit mark.</summary>
    private void OnEditsSaved(string path)
    {
        if (Photos.FirstOrDefault(p => p.IsSamePath(path)) is { } item)
            _ = Task.Run(() => LoadThumbnail(item));
    }

    private int CurrentIndex => CurrentPhoto is null ? -1 : Photos.IndexOf(CurrentPhoto);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPhoto() => CurrentPhoto = Photos[CurrentIndex + 1];

    private bool CanGoNext() => CurrentIndex >= 0 && CurrentIndex < Photos.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPhoto() => CurrentPhoto = Photos[CurrentIndex - 1];

    private bool CanGoPrevious() => CurrentIndex > 0;
}
