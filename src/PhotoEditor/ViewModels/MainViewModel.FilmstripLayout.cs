using System;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PhotoEditor.ViewModels;

/// <summary>
/// The filmstrip's size: its thumbnails can be hidden (F6, like Lightroom) to give the photo more room, or made
/// smaller / larger by dragging the strip's top edge. Remembered between sessions.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Thumbnail height range in the filmstrip (the cached thumbnails are 320 px on the long side).</summary>
    public const double MinFilmstripThumb = 48, MaxFilmstripThumb = 200, DefaultFilmstripThumb = 92;

    /// <summary>The thumbnails are shown (false = only the bar with the filter, stars and flags).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilmstripToggleText))]
    public partial bool IsFilmstripVisible { get; set; } = true;

    /// <summary>Height of a thumbnail in the filmstrip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilmstripItemWidth), nameof(FilmstripHeight))]
    public partial double FilmstripThumbHeight { get; set; } = DefaultFilmstripThumb;

    /// <summary>Width of a filmstrip item (thumbnail box, landscape 4 : 3 and a bit).</summary>
    public double FilmstripItemWidth => Math.Round(FilmstripThumbHeight * 116 / 92);

    /// <summary>Height of the thumbnail row: thumbnail, name and the scroll bar.</summary>
    public double FilmstripHeight => FilmstripThumbHeight + 40;

    public string FilmstripToggleText => IsFilmstripVisible ? "▾ Hide thumbnails" : "▴ Show thumbnails";

    /// <summary>Hides / shows the thumbnails (F6).</summary>
    [RelayCommand]
    private void ToggleFilmstrip() => IsFilmstripVisible = !IsFilmstripVisible;

    /// <summary>
    /// Resizes the thumbnails (dragging the strip's top edge), within the allowed range; remembered when
    /// <paramref name="done"/> (the drag ended).
    /// </summary>
    public void ResizeFilmstrip(double thumbHeight, bool done)
    {
        _resizingFilmstrip = !done;
        IsFilmstripVisible = true;
        FilmstripThumbHeight = Math.Clamp(Math.Round(thumbHeight), MinFilmstripThumb, MaxFilmstripThumb);
        if (done)
            SaveFilmstripLayout();
    }

    private bool _resizingFilmstrip;

    partial void OnIsFilmstripVisibleChanged(bool value) => SaveFilmstripLayout();

    partial void OnFilmstripThumbHeightChanged(double value) => SaveFilmstripLayout();

    // ---- Remembered between sessions ----

    private static readonly string FilmstripLayoutFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "filmstrip.json");

    private sealed record FilmstripLayout(bool Visible, double ThumbHeight);

    private bool _loadingFilmstripLayout;

    private void LoadFilmstripLayout()
    {
        try
        {
            if (!File.Exists(FilmstripLayoutFile))
                return;
            var layout = JsonSerializer.Deserialize<FilmstripLayout>(File.ReadAllText(FilmstripLayoutFile));
            if (layout is null || !double.IsFinite(layout.ThumbHeight))
                return;
            _loadingFilmstripLayout = true;
            IsFilmstripVisible = layout.Visible;
            FilmstripThumbHeight = Math.Clamp(layout.ThumbHeight, MinFilmstripThumb, MaxFilmstripThumb);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: keep the defaults.
        }
        finally
        {
            _loadingFilmstripLayout = false;
        }
    }

    private void SaveFilmstripLayout()
    {
        if (_loadingFilmstripLayout || _resizingFilmstrip)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilmstripLayoutFile)!);
            File.WriteAllText(FilmstripLayoutFile, JsonSerializer.Serialize(new FilmstripLayout(IsFilmstripVisible, FilmstripThumbHeight)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered this time; nothing else depends on it.
        }
    }
}
