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
                isExpanded: g.Key is AdjustmentParameters.Light or AdjustmentParameters.Color)
            {
                IsGlobalOnly = g.All(p => AdjustmentParameters.GlobalOnly.Contains(p.Parameter)),
            })
            .ToList();
        LoadPresets();
    }

    /// <summary>Full-resolution decoded original (never modified).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(CropSizeText))]
    [NotifyCanExecuteChangedFor(nameof(PasteSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
    public partial SKBitmap? Original { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial PreviewImage? Preview { get; private set; }

    /// <summary>Something is shown (maybe only the camera's preview of a RAW that is still decoding).</summary>
    public bool HasPreview => Preview is not null;

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
        foreach (var group in Groups.Where(g => g.IsGlobalOnly))
            group.IsVisible = value is null;
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
    [NotifyPropertyChangedFor(nameof(IsCropActive))]
    [NotifyPropertyChangedFor(nameof(IsObjectSelectActive))]
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

    /// <summary>When on, the viewer shows the whole image and the crop frame can be dragged.</summary>
    public bool IsCropActive
    {
        get => ActiveTool == EditTool.Crop;
        set => SetTool(EditTool.Crop, value);
    }

    private void SetTool(EditTool tool, bool on)
    {
        if (on)
            ActiveTool = tool;
        else if (ActiveTool == tool)
            ActiveTool = EditTool.None;
    }

    // ---- Crop ----

    /// <summary>Aspect ratio choices for the crop (Ratio = width / height; null = free, 0 = original photo).</summary>
    public sealed record CropAspect(string Name, double? Ratio)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<CropAspect> CropAspects { get; } =
    [
        new("Free", null), new("Original", 0), new("1 : 1", 1), new("3 : 2", 1.5), new("4 : 3", 4.0 / 3),
        new("5 : 4", 1.25), new("7 : 5", 1.4), new("16 : 9", 16.0 / 9),
    ];

    [ObservableProperty]
    public partial CropAspect SelectedCropAspect { get; set; } = new("Free", null);

    /// <summary>The locked width / height of the crop in pixels (portrait crops use the inverse ratio), or null.</summary>
    private double? LockedAspect
    {
        get
        {
            if (SelectedCropAspect.Ratio is not { } ratio || Original is not { } image)
                return null;
            if (ratio == 0)
                ratio = (double)image.Width / image.Height;
            var (w, h) = State.Crop.OutputSize(image.Width, image.Height);
            bool portrait = h > w;
            return (portrait ? ratio < 1 : ratio >= 1) ? ratio : 1 / ratio;
        }
    }

    partial void OnSelectedCropAspectChanged(CropAspect value)
    {
        if (LockedAspect is { } aspect && Original is { } image)
            ApplyEdit(State with { Crop = CropGeometry.WithAspect(State.Crop, aspect, image.Width, image.Height) });
    }

    /// <summary>Straighten angle in degrees (the crop shrinks so it stays inside the image).</summary>
    public double CropAngle
    {
        get => State.Crop.Angle;
        set
        {
            if (Original is not { } image || Math.Abs(value - State.Crop.Angle) < 1e-9)
                return;
            // Rotate the crop as it was when straightening started, so turning back restores its size.
            _cropAngleBase ??= State.Crop;
            _settingCropAngle = true;
            ApplyEdit(State with { Crop = CropGeometry.WithAngle(_cropAngleBase, Math.Round(value, 1), image.Width, image.Height) }, "crop-angle");
            _settingCropAngle = false;
        }
    }

    private Crop? _cropAngleBase;
    private bool _settingCropAngle;

    /// <summary>"6000 × 4000" (pixels of the export).</summary>
    public string CropSizeText
    {
        get
        {
            if (Original is not { } image)
                return "";
            var (w, h) = State.Crop.OutputSize(image.Width, image.Height);
            return $"{w} × {h}";
        }
    }

    public bool HasCrop => !State.Crop.IsDefault;

    [RelayCommand]
    private void ResetCrop() => ApplyEdit(State with { Crop = Crop.None });

    /// <summary>Swaps the crop between landscape and portrait.</summary>
    [RelayCommand]
    private void SwapCropOrientation()
    {
        if (Original is not { } image)
            return;
        var crop = State.Crop;
        if (crop.IsDefault && LockedAspect is null)
            crop = CropGeometry.WithAspect(crop, (double)image.Height / image.Width, image.Width, image.Height);
        else
            crop = CropGeometry.SwapOrientation(crop, image.Width, image.Height);
        ApplyEdit(State with { Crop = crop });
    }

    private Crop _cropDragStart = Crop.None;
    private string? _cropDragKey;

    /// <summary>Handles crop frame drags from the viewer (normalised coordinates).</summary>
    public void EditCrop(CropHandle handle, (double X, double Y) from, (double X, double Y) to, bool begin)
    {
        if (Original is not { } image)
            return;
        if (begin)
        {
            _cropDragStart = State.Crop;
            _cropDragKey = $"crop:{Guid.NewGuid()}";
            return;
        }
        var crop = CropGeometry.Drag(_cropDragStart, handle, from, to, LockedAspect, image.Width, image.Height);
        if (crop != State.Crop)
            ApplyEdit(State with { Crop = crop }, _cropDragKey);
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
        && mask.Components[SelectedComponentIndex] is (LinearGradientComponent or RadialGradientComponent or RasterMaskComponent) and var component
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

    partial void OnStateChanged(EditState oldValue, EditState newValue)
    {
        SyncMasks();
        RefreshSliders();
        if (oldValue.Adjustments.DenoiseAmount != newValue.Adjustments.DenoiseAmount
            || oldValue.Adjustments.DeblurAmount != newValue.Adjustments.DeblurAmount)
            ScheduleRestoreUpdate();
        if (oldValue.Crop != newValue.Crop)
        {
            if (!_settingCropAngle)
                _cropAngleBase = null;
            OnPropertyChanged(nameof(CropAngle));
            OnPropertyChanged(nameof(CropSizeText));
            OnPropertyChanged(nameof(HasCrop));
            ResetCropCommand.NotifyCanExecuteChanged();
        }
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
        if (Original is null)
            return; // nothing open, or only the camera preview of a RAW that is still decoding
        _history.Record(state, coalesceKey);
        State = state;
        UpdateHistoryCommands();
        _hasUnsavedEdits = true;
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

    /// <summary>True when the edit changed since it was loaded or last saved.</summary>
    private bool _hasUnsavedEdits;

    /// <summary>Upright size and EXIF orientation of the open image (for Lightroom's sensor-oriented coordinates).</summary>
    private ImageGeometry _geometry;

    /// <summary>What the last Lightroom XMP write could not include (reported when it changes).</summary>
    private string _lastXmpSkipped = "";

    /// <summary>
    /// Writes the sidecars now if anything changed (cancels a pending auto-save): the app's JSON and a
    /// Lightroom-compatible XMP. No file is created for an unedited image; the photo itself is never changed.
    /// </summary>
    [RelayCommand]
    public void SaveEdits()
    {
        _pendingSave?.Cancel();
        _pendingSave = null;
        if (FilePath is not { } path || !_hasUnsavedEdits)
            return;
        try
        {
            var skipped = string.Join("; ", EditStore.Save(path, State, _geometry));
            if (skipped != _lastXmpSkipped && skipped.Length > 0)
                Status = $"{Status} — not in the Lightroom XMP: {skipped}";
            _lastXmpSkipped = skipped;
            _hasUnsavedEdits = false;
            OnEditsSaved(path);
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
        _hasUnsavedEdits = true;
        ScheduleSave();
    }

    private bool CanUndo() => _history.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        State = _history.Redo();
        UpdateHistoryCommands();
        _hasUnsavedEdits = true;
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

    // ---- Copy / paste settings ----

    [ObservableProperty] public partial bool CopyLight { get; set; } = true;
    [ObservableProperty] public partial bool CopyColor { get; set; } = true;
    [ObservableProperty] public partial bool CopyHsl { get; set; } = true;
    [ObservableProperty] public partial bool CopyVignette { get; set; } = true;
    [ObservableProperty] public partial bool CopyDetail { get; set; } = true;
    [ObservableProperty] public partial bool CopyCrop { get; set; }
    [ObservableProperty] public partial bool CopyMasks { get; set; }

    private SettingsGroups SelectedCopyGroups =>
        (CopyLight ? SettingsGroups.Light : 0) | (CopyColor ? SettingsGroups.Color : 0) | (CopyHsl ? SettingsGroups.Hsl : 0)
        | (CopyVignette ? SettingsGroups.Vignette : 0) | (CopyDetail ? SettingsGroups.Detail : 0)
        | (CopyCrop ? SettingsGroups.Crop : 0) | (CopyMasks ? SettingsGroups.Masks : 0);

    private EditState? _copiedState;
    private SettingsGroups _copiedGroups;

    /// <summary>True once settings were copied (they stay available when another photo is opened).</summary>
    public bool HasCopiedSettings => _copiedState is not null;

    /// <summary>Remembers the chosen parts of this photo's edit for pasting.</summary>
    [RelayCommand]
    private void CopySettings()
    {
        if (!HasImage)
            return;
        _copiedState = State;
        _copiedGroups = SelectedCopyGroups;
        OnPropertyChanged(nameof(HasCopiedSettings));
        PasteSettingsCommand.NotifyCanExecuteChanged();
        Status = _copiedGroups == SettingsGroups.None ? "Nothing selected to copy." : $"Copied: {Describe(_copiedGroups)}";
    }

    /// <summary>Pastes the copied settings onto the open photo (one undo step).</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteSettings()
    {
        if (_copiedState is not { } copied || Original is not { } image)
            return;
        if (_copiedGroups.HasFlag(SettingsGroups.Masks))
            SelectedMask = null;
        ApplyEdit(SettingsTransfer.Apply(State, copied, _copiedGroups, image.Width, image.Height));
        Status = $"Pasted: {Describe(_copiedGroups)}";
    }

    private bool CanPaste() => HasCopiedSettings && HasImage;

    /// <summary>"Light, Color, HSL" for the status bar.</summary>
    private static string Describe(SettingsGroups groups) => string.Join(", ",
        new (SettingsGroups Flag, string Name)[]
        {
            (SettingsGroups.Light, "Light"), (SettingsGroups.Color, "Color"), (SettingsGroups.Hsl, "HSL"),
            (SettingsGroups.Vignette, "Vignette"), (SettingsGroups.Detail, "Detail"), (SettingsGroups.Crop, "Crop"),
            (SettingsGroups.Masks, "Masks"),
        }.Where(g => groups.HasFlag(g.Flag)).Select(g => g.Name));

    /// <summary>
    /// Pastes the copied settings into other photos without opening them: their JSON and Lightroom XMP
    /// sidecars are updated; the photos themselves are not changed. The open photo is updated in place.
    /// </summary>
    public async Task PasteToFilesAsync(IReadOnlyList<string> paths)
    {
        if (_copiedState is not { } copied || paths.Count == 0)
            return;
        var groups = _copiedGroups;
        var others = paths.Where(p => !ImageExporter.IsSameFile(p, FilePath)).ToList();
        if (others.Count < paths.Count)
            PasteSettings();

        var failed = new List<string>();
        for (int i = 0; i < others.Count; i++)
        {
            Status = $"Pasting settings… {i + 1} / {others.Count}";
            var result = await Task.Run(() => SettingsTransfer.PasteToFile(others[i], copied, groups));
            if (result.Error is not null)
                failed.Add($"{Path.GetFileName(result.ImagePath)} ({result.Error})");
        }
        int done = paths.Count - failed.Count;
        Status = failed.Count == 0
            ? $"Pasted settings into {done} photo{(done == 1 ? "" : "s")}."
            : $"Pasted into {done} of {paths.Count}; failed: {string.Join(", ", failed)}";
    }

    /// <summary>Sets the global Light sliders, white balance and vibrance from the photo's statistics (one undo step).</summary>
    [RelayCommand]
    private void Auto()
    {
        if (Original is not { } image)
            return;
        var suggested = AutoAdjust.Suggest(image, State.Crop, State.Adjustments);
        SelectedMask = null; // show the global sliders that changed
        ApplyEdit(State with { Adjustments = suggested });
        var a = suggested;
        Status = FormattableString.Invariant(
            $"Auto: exposure {a.Exposure:+0.00;-0.00;0}, contrast {a.Contrast:+0;-0;0}, highlights {a.Highlights:+0;-0;0}, shadows {a.Shadows:+0;-0;0}, whites {a.Whites:+0;-0;0}, blacks {a.Blacks:+0;-0;0}, temp {a.Temperature:+0;-0;0}, tint {a.Tint:+0;-0;0}, vibrance {a.Vibrance:+0;-0;0}");
    }

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
            if (await ExportSourceAsync(original, (state.Adjustments.DenoiseAmount / 100, state.Adjustments.DeblurAmount / 100)) is not { } exportSource)
            {
                Status = "Export stopped: the AI denoise / deblur did not finish.";
                return;
            }
            Status = $"Exporting {Path.GetFileName(path)}…";
            await Task.Run(() => ImageExporter.Export(exportSource, state, source, path, options));
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

    /// <summary>The app's own sidecar if there is one, otherwise Lightroom / Camera Raw edits from an XMP sidecar.</summary>
    private static (EditState State, string Note) LoadSidecar(string imagePath, ImageGeometry geometry)
    {
        try
        {
            var (state, source) = EditStore.Load(imagePath, geometry);
            return (state, source switch
            {
                EditSource.Json => " – edits loaded",
                EditSource.Xmp => " – edits imported from Lightroom (.xmp)",
                _ => "",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            return (EditState.Default, $" – could not read saved edits: {ex.Message}");
        }
    }
}
