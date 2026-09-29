using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using PhotoEditor.Core.Masks;
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
    [NotifyPropertyChangedFor(nameof(OverlayMask))]
    [NotifyPropertyChangedFor(nameof(EditableComponent))]
    public partial bool ShowOriginal { get; set; }

    /// <summary>What the viewer renders (respects the before/after toggle).</summary>
    public EditState DisplayState => ShowOriginal ? EditState.Default : State;

    /// <summary>The adjustment set the sliders currently edit: the selected mask's, or the global one.</summary>
    public AdjustmentSettings CurrentAdjustments =>
        SelectedMask is { } item && State.FindMask(item.Id) is { } mask ? mask.Adjustments : State.Adjustments;

    private EditState WithCurrentAdjustments(AdjustmentSettings settings) =>
        SelectedMask is { } item && State.FindMask(item.Id) is not null
            ? State.UpdateMask(item.Id, m => m with { Adjustments = settings })
            : State with { Adjustments = settings };

    // ---- Masks ----

    public ObservableCollection<MaskItemViewModel> Masks { get; } = [];

    /// <summary>Selected mask; the sliders edit its adjustments. Null = whole image.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMask))]
    [NotifyPropertyChangedFor(nameof(EditingLabel))]
    [NotifyPropertyChangedFor(nameof(OverlayMask))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMaskCommand))]
    public partial MaskItemViewModel? SelectedMask { get; set; }

    public bool HasSelectedMask => SelectedMask is not null;

    public ObservableCollection<ComponentItemViewModel> SelectedMaskComponents { get; } = [];

    public bool SelectedMaskHasNoComponents => HasSelectedMask && SelectedMaskComponents.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayMask))]
    public partial bool ShowMaskOverlay { get; set; } = true;

    /// <summary>Mask the viewer tints red (the selected one, when the overlay is on).</summary>
    public Mask? OverlayMask =>
        ShowMaskOverlay && !ShowOriginal && (_strokeMaskId ?? SelectedMask?.Id) is { } id ? State.FindMask(id) : null;

    public string EditingLabel => SelectedMask is { } m ? $"Editing mask: {m.Name}" : "Editing: whole image";

    partial void OnSelectedMaskChanged(MaskItemViewModel? value)
    {
        RefreshSliders();
        SelectedComponentIndex = -1;
        SelectedMaskComponents.Clear();
        SyncComponents();
        OnPropertyChanged(nameof(EditableComponent));
    }

    [RelayCommand]
    private void NewMask()
    {
        var mask = new Mask { Name = State.NextMaskName() };
        ApplyEdit(State.AddMask(mask));
        SelectedMask = Masks.FirstOrDefault(m => m.Id == mask.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMask))]
    private void DeleteMask()
    {
        if (SelectedMask is { } item)
            ApplyEdit(State.RemoveMask(item.Id));
    }

    [RelayCommand]
    private void EditWholeImage() => SelectedMask = null;

    [RelayCommand]
    private void ToggleMaskOverlay() => ShowMaskOverlay = !ShowMaskOverlay;

    // ---- Brush ----

    /// <summary>What left-dragging on the image does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushActive))]
    [NotifyPropertyChangedFor(nameof(IsLinearGradientActive))]
    [NotifyPropertyChangedFor(nameof(IsRadialGradientActive))]
    public partial EditTool ActiveTool { get; set; }

    /// <summary>When on, left-dragging on the image paints into the selected mask.</summary>
    public bool IsBrushActive
    {
        get => ActiveTool == EditTool.Brush;
        set => SetTool(EditTool.Brush, value);
    }

    /// <summary>When on, dragging on the image creates a linear gradient.</summary>
    public bool IsLinearGradientActive
    {
        get => ActiveTool == EditTool.LinearGradient;
        set => SetTool(EditTool.LinearGradient, value);
    }

    /// <summary>When on, dragging on the image creates a radial gradient.</summary>
    public bool IsRadialGradientActive
    {
        get => ActiveTool == EditTool.RadialGradient;
        set => SetTool(EditTool.RadialGradient, value);
    }

    private void SetTool(EditTool tool, bool on)
    {
        if (on)
            ActiveTool = tool;
        else if (ActiveTool == tool)
            ActiveTool = EditTool.None;
    }

    // ---- Gradients (created by dragging, edited with on-canvas handles) ----

    /// <summary>Index of the selected component of the selected mask (-1 = none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditableComponent))]
    public partial int SelectedComponentIndex { get; set; } = -1;

    /// <summary>The selected component if it is a gradient: the viewer shows its handles.</summary>
    public MaskComponent? EditableComponent =>
        !ShowOriginal && SelectedMask is { } item && State.FindMask(item.Id) is { } mask
        && SelectedComponentIndex >= 0 && SelectedComponentIndex < mask.Components.Count
        && mask.Components[SelectedComponentIndex] is (LinearGradientComponent or RadialGradientComponent) and var component
            ? component
            : null;

    private string? _componentEditKey;
    private Guid? _componentEditMaskId;
    private int _componentEditIndex = -1;
    private bool _componentEditIsNew;

    /// <summary>Handles gradient creation / handle drags from the viewer.</summary>
    public void EditComponent(MaskComponent? component, bool begin, bool end, bool isNew)
    {
        if (!HasImage)
            return;
        if (begin)
        {
            _componentEditKey = $"component:{Guid.NewGuid()}";
            _componentEditIsNew = isNew;
            if (isNew && component is not null)
            {
                // Add to the selected mask, or to a new one (selected when the drag ends).
                Guid maskId;
                if (SelectedMask is { } selected)
                {
                    maskId = selected.Id;
                }
                else
                {
                    var created = new Mask { Name = State.NextMaskName() };
                    ApplyEdit(State.AddMask(created), _componentEditKey);
                    maskId = created.Id;
                }
                EditMask(maskId, m => m.AddComponent(component), _componentEditKey);
                _componentEditMaskId = maskId;
                _componentEditIndex = State.FindMask(maskId)!.Components.Count - 1;
            }
            else
            {
                _componentEditMaskId = SelectedMask?.Id;
                _componentEditIndex = SelectedComponentIndex;
            }
            return;
        }

        if (_componentEditMaskId is { } id && _componentEditIndex >= 0 && component is not null)
        {
            if (end && _componentEditIsNew)
            {
                // A click without dragging: use a default size.
                if (component is LinearGradientComponent g && IsTiny(g))
                    component = g with { End = new BrushPoint(g.Start.X, g.Start.Y + 0.25f) };
                else if (component is RadialGradientComponent r && r.RadiusX + r.RadiusY < 0.01f)
                    component = r with { RadiusX = 0.2f, RadiusY = 0.15f };
            }
            int index = _componentEditIndex;
            EditMask(id, m => index < m.Components.Count ? m.ReplaceComponent(index, component) : m, _componentEditKey);
        }

        if (end)
        {
            if (_componentEditIsNew && _componentEditMaskId is { } maskId)
            {
                if (SelectedMask?.Id != maskId)
                    SelectedMask = Masks.FirstOrDefault(m => m.Id == maskId);
                SelectedComponentIndex = _componentEditIndex;
                ActiveTool = EditTool.None; // now the handles can be dragged
            }
            _componentEditKey = null;
            _componentEditMaskId = null;
            _componentEditIndex = -1;
            OnPropertyChanged(nameof(EditableComponent));
        }
    }

    private static bool IsTiny(LinearGradientComponent g) =>
        Math.Abs(g.End.X - g.Start.X) + Math.Abs(g.End.Y - g.Start.Y) < 0.01f;

    /// <summary>1..100; radius = size × 0.2% of the image's longer side.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushRadius))]
    public partial double BrushSize { get; set; } = 15;

    /// <summary>0..100 (%).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushFeatherFraction))]
    public partial double BrushFeather { get; set; } = 50;

    /// <summary>1..100 (%).</summary>
    [ObservableProperty]
    public partial double BrushFlow { get; set; } = 100;

    [ObservableProperty]
    public partial bool BrushErase { get; set; }

    /// <summary>Brush radius as a fraction of the image's longer side.</summary>
    public double BrushRadius => BrushSize * 0.002;

    public double BrushFeatherFraction => BrushFeather / 100;

    private string? _strokeKey;
    private Guid? _strokeMaskId;
    private BrushPoint _lastStrokePoint;

    /// <summary>Starts a brush stroke at normalised image coordinates; creates a mask if none is selected.</summary>
    public void BeginStroke(double x, double y, bool invertErase)
    {
        if (!HasImage)
            return;
        Guid maskId;
        if (SelectedMask is { } selected)
        {
            maskId = selected.Id;
        }
        else
        {
            // New mask; it is selected when the stroke ends (changing the list selection while the
            // viewer holds the pointer capture broke later clicks).
            var created = new Mask { Name = State.NextMaskName() };
            ApplyEdit(State.AddMask(created));
            maskId = created.Id;
        }
        _strokeMaskId = maskId;

        var point = new BrushPoint((float)x, (float)y);
        var stroke = new BrushStroke
        {
            Radius = (float)BrushRadius,
            Feather = (float)BrushFeatherFraction,
            Flow = (float)(BrushFlow / 100),
            Erase = BrushErase ^ invertErase,
            Points = [point],
        };
        _strokeKey = $"stroke:{Guid.NewGuid()}";
        _lastStrokePoint = point;
        EditMask(maskId, m => m.Components.Count > 0 && m.Components[^1] is BrushComponent brush
            ? m.ReplaceComponent(m.Components.Count - 1, brush.AddStroke(stroke))
            : m.AddComponent(new BrushComponent().AddStroke(stroke)), _strokeKey);
    }

    public void ContinueStroke(double x, double y)
    {
        if (_strokeKey is null || _strokeMaskId is not { } maskId)
            return;
        var point = new BrushPoint((float)x, (float)y);
        // Skip points closer than a tenth of the radius; the rasteriser interpolates between points.
        float dx = point.X - _lastStrokePoint.X, dy = point.Y - _lastStrokePoint.Y;
        double minStep = BrushRadius * 0.1;
        if (dx * dx + dy * dy < minStep * minStep)
            return;
        _lastStrokePoint = point;
        EditMask(maskId, m => m.Components.Count > 0 && m.Components[^1] is BrushComponent brush
            ? m.ReplaceComponent(m.Components.Count - 1, brush.ExtendLastStroke(point))
            : m, _strokeKey);
    }

    public void EndStroke()
    {
        _strokeKey = null;
        if (_strokeMaskId is { } id && SelectedMask?.Id != id)
            SelectedMask = Masks.FirstOrDefault(m => m.Id == id);
        _strokeMaskId = null;
        OnPropertyChanged(nameof(OverlayMask));
    }

    private void EditMask(Guid id, Func<Mask, Mask> update, string? key) => ApplyEdit(State.UpdateMask(id, update), key);

    private void EditSelectedComponent(int index, Func<MaskComponent, MaskComponent> update, string? key)
    {
        if (SelectedMask is { } item)
            EditMask(item.Id, m => index < m.Components.Count ? m.ReplaceComponent(index, update(m.Components[index])) : m, key);
    }

    private void DeleteSelectedComponent(int index)
    {
        if (SelectedMask is { } item)
            EditMask(item.Id, m => index < m.Components.Count ? m.RemoveComponent(index) : m, null);
    }

    /// <summary>Brings the mask list in line with <see cref="State"/>, keeping the selection when possible.</summary>
    private void SyncMasks()
    {
        var masks = State.Masks;
        if (!masks.Select(m => m.Id).SequenceEqual(Masks.Select(m => m.Id)))
        {
            var selectedId = SelectedMask?.Id;
            Masks.Clear();
            foreach (var mask in masks)
                Masks.Add(new MaskItemViewModel(mask, EditMask));
            SelectedMask = Masks.FirstOrDefault(m => m.Id == selectedId);
        }
        else
        {
            for (int i = 0; i < masks.Count; i++)
                Masks[i].Update(masks[i]);
        }
        SyncComponents();
        OnPropertyChanged(nameof(OverlayMask));
        OnPropertyChanged(nameof(EditingLabel));
        OnPropertyChanged(nameof(EditableComponent));
    }

    private void SyncComponents()
    {
        var components = SelectedMask is { } item && State.FindMask(item.Id) is { } mask ? mask.Components : [];
        // Rebuild only when something shown in the list changed (not when a brush stroke grows).
        bool same = components.Count == SelectedMaskComponents.Count
            && components.Select((c, i) => (c, i)).All(x => SelectedMaskComponents[x.i].Shows(x.c));
        if (same)
            return;
        int keep = SelectedComponentIndex;
        SelectedMaskComponents.Clear();
        for (int i = 0; i < components.Count; i++)
            SelectedMaskComponents.Add(new ComponentItemViewModel(i, components[i], EditSelectedComponent, DeleteSelectedComponent));
        SelectedComponentIndex = keep >= 0 && keep < components.Count ? keep : components.Count - 1;
        OnPropertyChanged(nameof(SelectedMaskHasNoComponents));
    }

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

    private AdjustmentSettings? _shownAdjustments;

    partial void OnStateChanged(EditState value)
    {
        SyncMasks();
        RefreshSliders();
    }

    /// <summary>Updates the sliders if the edited adjustment set changed (not on every brush point).</summary>
    private void RefreshSliders()
    {
        var current = CurrentAdjustments;
        if (current == _shownAdjustments)
            return;
        _shownAdjustments = current;
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
            SelectedMask = null;
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
