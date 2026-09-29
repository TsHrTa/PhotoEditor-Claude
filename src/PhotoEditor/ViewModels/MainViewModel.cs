using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        var parameters = AdjustmentParameters.All
            .Select(p => new ParameterViewModel(p, () => CurrentAdjustments, s => ApplyEdit(WithCurrentAdjustments(s), p.ToString())))
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
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial SKBitmap? Original { get; private set; }

    [ObservableProperty]
    public partial PreviewImage? Preview { get; private set; }

    private readonly EditHistory<EditState> _history = new(EditState.Default);

    /// <summary>The whole edit (global adjustments + masks). Set through <see cref="ApplyEdit"/> so the change is undoable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayState))]
    [NotifyCanExecuteChangedFor(nameof(ResetAllCommand))]
    public partial EditState State { get; private set; } = EditState.Default;

    /// <summary>When true the viewer shows the unedited original ("before").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayState))]
    public partial bool ShowOriginal { get; set; }

    /// <summary>What the viewer renders (respects the before/after toggle).</summary>
    public EditState DisplayState => ShowOriginal ? EditState.Default : State;

    /// <summary>The adjustment set the sliders currently edit.</summary>
    public AdjustmentSettings CurrentAdjustments => State.Adjustments;

    private EditState WithCurrentAdjustments(AdjustmentSettings settings) => State with { Adjustments = settings };

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    /// <summary>JPEG export quality, 1..100.</summary>
    [ObservableProperty]
    public partial int JpegQuality { get; set; } = 90;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial bool IsExporting { get; private set; }

    public bool CanExport => HasImage && !IsExporting;

    [ObservableProperty]
    public partial string Status { get; set; } = "Open an image (Ctrl+O) or drop one onto the window.";

    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    public IReadOnlyList<AdjustmentGroupViewModel> Groups { get; }

    public bool HasImage => Original is not null;

    public string Title => FilePath is null ? "PhotoEditor" : $"{Path.GetFileName(FilePath)} – PhotoEditor";

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    partial void OnStateChanged(EditState value)
    {
        OnPropertyChanged(nameof(CurrentAdjustments));
        foreach (var p in Parameters)
            p.Refresh();
    }

    /// <summary>
    /// Applies an edit and records it for undo. Edits with the same <paramref name="coalesceKey"/>
    /// in quick succession (a slider drag) form one undo step.
    /// </summary>
    public void ApplyEdit(EditState state, string? coalesceKey = null)
    {
        _history.Record(state, coalesceKey);
        State = state;
        UpdateHistoryCommands();
        ScheduleSave();
    }

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _pendingSave;

    /// <summary>Saves the sidecar shortly after the last change (edits are auto-saved).</summary>
    private void ScheduleSave()
    {
        _pendingSave?.Cancel();
        if (FilePath is null)
            return;
        var cts = _pendingSave = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(cts.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDelay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        SaveEdits();
    }

    /// <summary>Writes the sidecar now (cancels a pending auto-save). No file is created for an unedited image.</summary>
    [RelayCommand]
    public void SaveEdits()
    {
        _pendingSave?.Cancel();
        _pendingSave = null;
        if (FilePath is not { } path || (State.IsDefault && !SidecarFile.Exists(path)))
            return;
        try
        {
            SidecarFile.Save(path, EditDocument.From(State));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save edits: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        State = _history.Undo();
        UpdateHistoryCommands();
        ScheduleSave();
    }

    private bool CanUndo() => _history.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        State = _history.Redo();
        UpdateHistoryCommands();
        ScheduleSave();
    }

    private bool CanRedo() => _history.CanRedo;

    private void UpdateHistoryCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleBeforeAfter() => ShowOriginal = !ShowOriginal;

    [RelayCommand(CanExecute = nameof(CanResetAll))]
    private void ResetAll() => ApplyEdit(EditState.Default);

    private bool CanResetAll() => !State.IsDefault;

    /// <summary>Suggested export file name, e.g. "IMG_0001-edited.jpg".</summary>
    public string SuggestedExportName =>
        FilePath is null ? "export.jpg" : Path.GetFileNameWithoutExtension(FilePath) + "-edited.jpg";

    /// <summary>Renders the full-resolution image with the current settings and writes it to <paramref name="path"/>.</summary>
    public async Task ExportAsync(string path)
    {
        if (Original is not { } original || IsExporting)
            return;
        var state = State;
        var source = FilePath;
        var options = new ExportOptions(ExportOptions.FormatFromPath(path), JpegQuality);
        IsExporting = true;
        Status = $"Exporting {Path.GetFileName(path)}…";
        try
        {
            var watch = Stopwatch.StartNew();
            await Task.Run(() => ImageExporter.Export(original, state, source, path, options));
            Status = $"Exported {Path.GetFileName(path)} ({watch.Elapsed.TotalSeconds:0.0} s)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static (EditState State, string Note) LoadSidecar(string imagePath)
    {
        try
        {
            return SidecarFile.Load(imagePath) is { } doc
                ? (doc.ToState(), " – edits loaded")
                : (EditState.Default, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return (EditState.Default, $" – could not read saved edits: {ex.Message}");
        }
    }

    /// <summary>Loads the image at <paramref name="path"/>; reports failures in <see cref="Status"/>.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            var bitmap = ImageLoader.Load(path);
            var preview = PreviewImage.Create(bitmap);
            SaveEdits(); // flush edits of the previous image
            var (state, sidecarNote) = LoadSidecar(path);
            Original = bitmap;
            Preview = preview;
            _history.Reset(state);
            State = state;
            UpdateHistoryCommands();
            ShowOriginal = false;
            FilePath = path;
            Status = $"{bitmap.Width} × {bitmap.Height}{sidecarNote}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }
}
