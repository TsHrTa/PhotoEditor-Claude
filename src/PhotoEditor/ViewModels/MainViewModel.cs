using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        var parameters = AdjustmentParameters.All
            .Select(p => new ParameterViewModel(p, () => Settings, s => Settings = s))
            .ToList();
        Parameters = parameters;
        Groups = parameters
            .GroupBy(p => p.Parameter.Group)
            .Select(g => new AdjustmentGroupViewModel(g.Key, g.ToList(),
                isExpanded: g.Key is AdjustmentParameters.Light or AdjustmentParameters.Color))
            .ToList();
    }

    /// <summary>Full-resolution decoded original (never modified).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial SKBitmap? Original { get; private set; }

    [ObservableProperty]
    public partial PreviewImage? Preview { get; private set; }

    [ObservableProperty]
    public partial AdjustmentSettings Settings { get; set; } = AdjustmentSettings.Default;

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Open an image (Ctrl+O) or drop one onto the window.";

    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    public IReadOnlyList<AdjustmentGroupViewModel> Groups { get; }

    public bool HasImage => Original is not null;

    public string Title => FilePath is null ? "PhotoEditor" : $"{Path.GetFileName(FilePath)} – PhotoEditor";

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    partial void OnSettingsChanged(AdjustmentSettings value)
    {
        foreach (var p in Parameters)
            p.Refresh();
    }

    /// <summary>Loads the image at <paramref name="path"/>; reports failures in <see cref="Status"/>.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            var bitmap = ImageLoader.Load(path);
            var preview = PreviewImage.Create(bitmap);
            Original = bitmap;
            Preview = preview;
            Settings = AdjustmentSettings.Default;
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
