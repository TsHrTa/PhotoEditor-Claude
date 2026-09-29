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
using PhotoEditor.Core.Editing;
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

    /// <summary>The photos the filmstrip shows (the folder's photos that pass <see cref="Filter"/>).</summary>
    public ObservableCollection<FilmstripItem> Photos { get; } = [];

    /// <summary>All photos of the folder.</summary>
    private List<FilmstripItem> _allPhotos = [];

    public IReadOnlyList<LabelFilterOption> FilterOptions { get; } =
        Enum.GetValues<LabelFilter>().Select(f => new LabelFilterOption(f, f.DisplayName())).ToList();

    [ObservableProperty]
    public partial LabelFilterOption? SelectedFilter { get; set; }

    private LabelFilter Filter => SelectedFilter?.Filter ?? LabelFilter.All;

    /// <summary>E.g. "12 of 150 photos".</summary>
    [ObservableProperty]
    public partial string FolderCountText { get; private set; } = "";

    partial void OnSelectedFilterChanged(LabelFilterOption? value)
    {
        ApplyFilter(FilePath);
        // The open photo stays until you move on; with nothing open yet, open the first one shown.
        if (CurrentPhoto is null && Photos.Count > 0)
            CurrentPhoto = Photos[0];
    }

    /// <summary>Shows the photos that pass the filter, plus <paramref name="keep"/> (the open photo) so the selection stays.</summary>
    private void ApplyFilter(string? keep)
    {
        var filter = Filter;
        var current = CurrentPhoto;
        _syncingFilmstrip = true;
        Photos.Clear();
        foreach (var item in _allPhotos.Where(p => filter.Matches(p.Labels) || p.IsSamePath(keep)))
            Photos.Add(item);
        CurrentPhoto = current is not null && Photos.Contains(current) ? current : null;
        _syncingFilmstrip = false;
        FolderCountText = filter == LabelFilter.All
            ? $"{_allPhotos.Count} photos"
            : $"{_allPhotos.Count(p => filter.Matches(p.Labels))} of {_allPhotos.Count} photos";
        NextPhotoCommand.NotifyCanExecuteChanged();
        PreviousPhotoCommand.NotifyCanExecuteChanged();
    }

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
        _allPhotos = files.Select(file => new FilmstripItem(file)
        {
            HasEdits = PhotoFolder.HasEdits(file),
            Labels = LoadLabels(file),
        }).ToList();
        CurrentPhoto = null;
        ApplyFilter(current);
        FolderPath = folder;
        HasFolder = _allPhotos.Count > 0;
        int start = Math.Max(0, _allPhotos.FindIndex(p => p.IsSamePath(current)));
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
        if (!Photos.Any(p => p.IsSamePath(path)))
            ApplyFilter(path); // opened from outside the filmstrip although the filter hides it
        _syncingFilmstrip = true;
        CurrentPhoto = Photos.FirstOrDefault(p => p.IsSamePath(path));
        _syncingFilmstrip = false;
    }

    /// <summary>Loads every thumbnail, nearest to <paramref name="start"/> first, two at a time.</summary>
    private async Task LoadThumbnailsAsync(int start, CancellationToken cancel)
    {
        var items = _allPhotos.ToList();
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
        if (_allPhotos.FirstOrDefault(p => p.IsSamePath(path)) is { } item)
            _ = Task.Run(() => LoadThumbnail(item));
    }

    private int CurrentIndex => CurrentPhoto is null ? -1 : Photos.IndexOf(CurrentPhoto);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPhoto() => CurrentPhoto = Photos[CurrentIndex + 1];

    private bool CanGoNext() => CurrentIndex >= 0 && CurrentIndex < Photos.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPhoto() => CurrentPhoto = Photos[CurrentIndex - 1];

    private bool CanGoPrevious() => CurrentIndex > 0;

    private static PhotoLabels LoadLabels(string path)
    {
        try
        {
            return EditStore.LoadLabels(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PhotoLabels.None;
        }
    }

    /// <summary>Sets the open photo's star rating (0 clears it).</summary>
    public void SetRating(int rating) =>
        UpdateLabels(l => l with { Rating = Math.Clamp(rating, 0, 5) });

    /// <summary>Sets the open photo's flag; setting the flag it already has clears it.</summary>
    public void SetFlag(PhotoFlag flag) =>
        UpdateLabels(l => l with { Flag = l.Flag == flag ? PhotoFlag.None : flag });

    /// <summary>
    /// Changes and saves the open photo's labels. If the photo no longer passes the filter, the next photo that
    /// does is opened (so rejecting with "Not rejected" shown works through a folder).
    /// </summary>
    private void UpdateLabels(Func<PhotoLabels, PhotoLabels> change)
    {
        if (CurrentPhoto is not { } item || FilePath is null || !item.IsSamePath(FilePath))
            return;
        var labels = change(item.Labels);
        if (labels == item.Labels)
            return;
        try
        {
            EditStore.SaveLabels(item.Path, labels);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Could not save the rating: {ex.Message}";
            return;
        }
        item.Labels = labels;
        Status = $"{item.Name}: {item.LabelText}";
        if (Filter.Matches(labels))
        {
            ApplyFilter(item.Path);
            return;
        }
        int index = Photos.IndexOf(item);
        var next = Photos.Skip(index + 1).FirstOrDefault(p => Filter.Matches(p.Labels))
            ?? Photos.Take(index).LastOrDefault(p => Filter.Matches(p.Labels));
        ApplyFilter(next?.Path ?? item.Path);
        if (next is not null)
            CurrentPhoto = next;
    }
}

/// <summary>An entry of the filmstrip's filter list.</summary>
public sealed record LabelFilterOption(LabelFilter Filter, string Name)
{
    public override string ToString() => Name;
}
