using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial SKBitmap? Image { get; set; }

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Open an image (Ctrl+O) or drop one onto the window.";

    public bool HasImage => Image is not null;

    public string Title => FilePath is null ? "PhotoEditor" : $"{Path.GetFileName(FilePath)} – PhotoEditor";

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    /// <summary>Loads the image at <paramref name="path"/>; reports failures in <see cref="Status"/>.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            var bitmap = ImageLoader.Load(path);
            Image = bitmap;
            FilePath = path;
            Status = $"{bitmap.Width} × {bitmap.Height}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }
}
