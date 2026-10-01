using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Export;

namespace PhotoEditor.ViewModels;

/// <summary>Which photos to export, e.g. "Picked photos (12)".</summary>
public sealed record ExportScope(string Name, IReadOnlyList<string> Photos)
{
    public override string ToString() => $"{Name} ({Photos.Count})";
}

public sealed record ExistingFileOption(ExistingFile Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The "Export photos" dialog: what to export, where and how.</summary>
public partial class BatchExportViewModel : ViewModelBase
{
    /// <summary>Remembered for the next export in this session.</summary>
    private static string? _lastFolder;
    private static bool _lastPng, _lastResize;
    private static int _lastLongEdge = 2048;
    private static string _lastSuffix = "";
    private static ExistingFile _lastIfExists = ExistingFile.Replace;

    public BatchExportViewModel(IReadOnlyList<ExportScope> scopes, string defaultFolder, int jpegQuality)
    {
        Scopes = scopes;
        SelectedScope = scopes.FirstOrDefault(s => s.Photos.Count > 0) ?? scopes.FirstOrDefault();
        Folder = _lastFolder ?? defaultFolder;
        IsPng = _lastPng;
        JpegQuality = jpegQuality;
        Resize = _lastResize;
        LongEdge = _lastLongEdge;
        Suffix = _lastSuffix;
        SelectedIfExists = IfExistsOptions.First(o => o.Value == _lastIfExists);
    }

    public IReadOnlyList<ExportScope> Scopes { get; }

    public IReadOnlyList<ExistingFileOption> IfExistsOptions { get; } =
    [
        new(ExistingFile.Replace, "Replace it (e.g. an earlier export)"),
        new(ExistingFile.KeepBoth, "Keep both (add a number)"),
        new(ExistingFile.Skip, "Skip that photo"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(SummaryText))]
    public partial ExportScope? SelectedScope { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(SummaryText))]
    public partial string Folder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpeg), nameof(SummaryText))]
    public partial bool IsPng { get; set; }

    public bool IsJpeg => !IsPng;

    [ObservableProperty]
    public partial int JpegQuality { get; set; }

    [ObservableProperty]
    public partial bool Resize { get; set; }

    [ObservableProperty]
    public partial int LongEdge { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial string Suffix { get; set; } = "";

    [ObservableProperty]
    public partial ExistingFileOption? SelectedIfExists { get; set; }

    public bool CanExport => SelectedScope is { Photos.Count: > 0 } && !string.IsNullOrWhiteSpace(Folder);

    /// <summary>E.g. "12 photos → D:\Photos\Export, named like IMG_0001-edited.jpg".</summary>
    public string SummaryText => SelectedScope is not { Photos.Count: > 0 } scope
        ? "No photos to export."
        : $"{scope.Photos.Count} photo{(scope.Photos.Count == 1 ? "" : "s")} → {Folder}, named like " +
          $"{Path.GetFileNameWithoutExtension(scope.Photos[0])}{Suffix}{(IsPng ? ".png" : ".jpg")}. The originals are not changed.";

    public BatchExportOptions Options()
    {
        _lastFolder = Folder;
        _lastPng = IsPng;
        _lastResize = Resize;
        _lastLongEdge = LongEdge;
        _lastSuffix = Suffix;
        _lastIfExists = SelectedIfExists?.Value ?? ExistingFile.Replace;
        var format = new ExportOptions(IsPng ? ExportFormat.Png : ExportFormat.Jpeg, JpegQuality,
            Resize && LongEdge > 0 ? LongEdge : null);
        return new BatchExportOptions(Folder.Trim(), format, Suffix, _lastIfExists);
    }
}
