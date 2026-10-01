using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Controls;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;

namespace PhotoEditor.ViewModels;

/// <summary>
/// Spot removal (Q): a click heals / clones a spot from an automatically chosen source; spots and sources can be
/// dragged. The viewer shows a retouched copy of the photo, rebuilt in the background when the spots change.
/// </summary>
public partial class MainViewModel
{
    /// <summary>When on, clicks on the image remove spots.</summary>
    public bool IsSpotActive
    {
        get => ActiveTool == EditTool.Spot;
        set => SetTool(EditTool.Spot, value);
    }

    /// <summary>The spot whose settings the sliders show (null = settings for new spots).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSpot))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSpotCommand))]
    public partial Guid? SelectedSpotId { get; set; }

    public bool HasSelectedSpot => SelectedSpotId is not null;

    public bool HasSpots => State.Spots.Count > 0;

    public string SpotCountText => State.Spots.Count switch { 0 => "No spots", 1 => "1 spot", var n => $"{n} spots" };

    // Settings for new spots; while a spot is selected the sliders show and change that spot.
    private SpotMode _newSpotMode = SpotMode.Heal;
    private float _newSpotRadius = 0.015f, _newSpotFeather = 0.5f, _newSpotOpacity = 1f;

    private Spot? SelectedSpot => SelectedSpotId is { } id ? State.Spots.Find(s => s.Id == id) : null;

    private SpotMode ShownSpotMode => SelectedSpot?.Mode ?? _newSpotMode;

    /// <summary>Heal: texture from the source, colours fitted to the surroundings.</summary>
    public bool SpotHeal
    {
        get => ShownSpotMode == SpotMode.Heal;
        set { if (value) SetSpotMode(SpotMode.Heal); }
    }

    /// <summary>Clone: an exact copy of the source.</summary>
    public bool SpotClone
    {
        get => ShownSpotMode == SpotMode.Clone;
        set { if (value) SetSpotMode(SpotMode.Clone); }
    }

    /// <summary>Remove (AI): paint over something; an inpainting model fills it in.</summary>
    public bool SpotRemove
    {
        get => ShownSpotMode == SpotMode.Remove;
        set { if (value) SetSpotMode(SpotMode.Remove); }
    }

    /// <summary>New spots are painted (Remove) rather than clicked (Heal / Clone).</summary>
    public bool IsSpotPainting => _newSpotMode == SpotMode.Remove;

    /// <summary>
    /// Sets the mode for new spots; a selected heal / clone spot switches between the two (a Remove spot stays one,
    /// and choosing Remove deselects a heal / clone spot so the next stroke paints).
    /// </summary>
    private void SetSpotMode(SpotMode mode)
    {
        _newSpotMode = mode;
        if (mode != SpotMode.Remove)
            ClearRemoveSelection();
        if (SelectedSpot is { } spot && (spot.Mode == SpotMode.Remove) != (mode == SpotMode.Remove))
            SelectedSpotId = null;
        else if (SelectedSpot is { } selected && selected.Mode != mode)
            ApplyEdit(UpdateSpot(State, selected.Id, s => s with { Mode = mode }));
        if (mode == SpotMode.Remove && HasImage)
            IsSpotActive = true; // ready to paint
        RefreshSpotSliders();
        OnPropertyChanged(nameof(IsSpotPainting));
    }

    /// <summary>Size: radius in % of the photo's longer side.</summary>
    public double SpotSize
    {
        get => Math.Round(SpotCursorRadius * 100, 2);
        set
        {
            float r = Math.Clamp((float)value / 100, Spot.MinRadius, Spot.MaxRadius);
            ChangeSpot(s => s with { Radius = r }, () => _newSpotRadius = r, "spot-size");
        }
    }

    /// <summary>Feather 0..100.</summary>
    public double SpotFeather
    {
        get => Math.Round((SelectedSpot?.Feather ?? _newSpotFeather) * 100);
        set
        {
            float f = Math.Clamp((float)value / 100, 0, 1);
            ChangeSpot(s => s with { Feather = f }, () => _newSpotFeather = f, "spot-feather");
        }
    }

    /// <summary>Opacity 0..100.</summary>
    public double SpotOpacity
    {
        get => Math.Round((SelectedSpot?.Opacity ?? _newSpotOpacity) * 100);
        set
        {
            float o = Math.Clamp((float)value / 100, 0, 1);
            ChangeSpot(s => s with { Opacity = o }, () => _newSpotOpacity = o, "spot-opacity");
        }
    }

    /// <summary>Radius of the cursor / a new spot, as a fraction of the longer side (for the viewer).</summary>
    public double SpotCursorRadius => SelectedSpot is { Mode: not SpotMode.Remove } s ? s.Radius : _newSpotRadius;

    /// <summary>Changes the selected spot (undoable) and the settings for new spots.</summary>
    private void ChangeSpot(Func<Spot, Spot> update, Action setDefault, string? key)
    {
        setDefault();
        // A Remove spot's fill was made for its stroke: only its opacity can change.
        if (SelectedSpot is { } spot && (spot.Mode != SpotMode.Remove || key == "spot-opacity"))
            ApplyEdit(UpdateSpot(State, spot.Id, update), key);
        RefreshSpotSliders();
    }

    private static EditState UpdateSpot(EditState state, Guid id, Func<Spot, Spot> update)
    {
        int i = state.Spots.FindIndex(s => s.Id == id);
        return i < 0 ? state : state with { Spots = state.Spots.SetItem(i, update(state.Spots[i])) };
    }

    private void RefreshSpotSliders()
    {
        foreach (var name in new[] { nameof(SpotHeal), nameof(SpotClone), nameof(SpotRemove), nameof(SpotSize), nameof(SpotFeather), nameof(SpotOpacity),
            nameof(SpotCursorRadius), nameof(HasSpots), nameof(SpotCountText) })
            OnPropertyChanged(name);
    }

    partial void OnSelectedSpotIdChanged(Guid? value) => RefreshSpotSliders();

    [RelayCommand(CanExecute = nameof(HasSelectedSpot))]
    private void DeleteSpot()
    {
        if (SelectedSpotId is { } id)
        {
            ApplyEdit(State with { Spots = State.Spots.RemoveAll(s => s.Id == id) });
            SelectedSpotId = null;
        }
    }

    [RelayCommand]
    private void RemoveAllSpots()
    {
        if (State.Spots.Count == 0)
            return;
        ApplyEdit(State with { Spots = [] });
        SelectedSpotId = null;
    }

    /// <summary>Viewer input with the spot tool.</summary>
    public void EditSpot(SpotEditKind kind, Guid? id, BrushPoint point, EditPhase phase)
    {
        if (Original is null)
            return;
        switch (kind)
        {
            case SpotEditKind.Add:
            {
                var image = EditBase!;
                var source = Retouching.FindSource(image, point, _newSpotRadius, State.Spots);
                var spot = new Spot
                {
                    Mode = _newSpotMode, Center = point, Source = source,
                    Radius = _newSpotRadius, Feather = _newSpotFeather, Opacity = _newSpotOpacity,
                };
                ApplyEdit(State with { Spots = State.Spots.Add(spot) });
                SelectedSpotId = spot.Id;
                break;
            }
            case SpotEditKind.Select:
                SelectedSpotId = id;
                break;
            case SpotEditKind.Move or SpotEditKind.MoveSource when id is { } spotId:
                if (phase == EditPhase.Begin)
                {
                    SelectedSpotId = spotId;
                    _spotDragKey = $"spot-drag-{Guid.NewGuid()}"; // one undo step per drag
                }
                ApplyEdit(UpdateSpot(State, spotId, s => kind == SpotEditKind.Move ? Moved(s, point) : s with { Source = point }),
                    _spotDragKey);
                break;
        }
    }

    private string? _spotDragKey;

    /// <summary>The spot moved so its centre is at <paramref name="center"/> (a painted stroke moves along; the source stays).</summary>
    private static Spot Moved(Spot spot, BrushPoint center)
    {
        float dx = center.X - spot.Center.X, dy = center.Y - spot.Center.Y;
        return spot with
        {
            Center = center,
            Path = spot.Path.Select(p => new BrushPoint(Math.Clamp(p.X + dx, 0f, 1f), Math.Clamp(p.Y + dy, 0f, 1f))).ToImmutableList(),
        };
    }

    /// <summary>
    /// A stroke painted with the spot tool: adds to (or with <paramref name="erase"/> takes away from) the AI Remove
    /// selection, or makes a painted heal / clone spot with an automatic source.
    /// </summary>
    public void Painted(IReadOnlyList<BrushPoint> path, bool erase)
    {
        if (_newSpotMode == SpotMode.Remove)
        {
            AddRemoveStroke(path, erase);
            return;
        }
        if (erase || Original is null || EditBase is not { } image || path.Count == 0)
            return;
        var points = SimplifiedPath(path, _newSpotRadius).ToList();
        var center = new BrushPoint(points.Average(p => p.X), points.Average(p => p.Y));
        var source = points.Count > 1
            ? Retouching.FindStrokeSource(image, points, _newSpotRadius, center)
            : Retouching.FindSource(image, center, _newSpotRadius, State.Spots);
        var spot = new Spot
        {
            Mode = _newSpotMode, Center = center, Source = source, Path = points.Count > 1 ? [.. points] : [],
            Radius = _newSpotRadius, Feather = _newSpotFeather, Opacity = _newSpotOpacity,
        };
        ApplyEdit(State with { Spots = State.Spots.Add(spot) });
        SelectedSpotId = spot.Id;
    }

    // ---- AI Remove ----

    private Inpainter? _inpainter;
    private Task<Inpainter>? _inpainterLoading;
    private bool _removing;

    /// <summary>
    /// Adds a stroke to the selection to remove (shown in red; nothing is filled yet). Erasing strokes take parts of
    /// it away again; erasing with no selection does nothing.
    /// </summary>
    private void AddRemoveStroke(IReadOnlyList<BrushPoint> path, bool erase)
    {
        if (Original is null || path.Count == 0 || _removing)
            return;
        var strokes = PendingRemove?.Strokes ?? [];
        if (erase && strokes.IsEmpty)
            return;
        strokes = strokes.Add(new RemoveStroke([.. SimplifiedPath(path, _newSpotRadius)], _newSpotRadius, erase));
        var painted = strokes.Where(s => !s.Erase).SelectMany(s => s.Path).ToList();
        PendingRemove = new Spot
        {
            Mode = SpotMode.Remove, Opacity = _newSpotOpacity, Strokes = strokes,
            Radius = strokes.Where(s => !s.Erase).Max(s => s.Radius),
            Center = new BrushPoint(painted.Average(p => p.X), painted.Average(p => p.Y)),
        };
        SelectedSpotId = null;
        Status = erase
            ? "Erased from the selection. Remove (Enter) fills it, Clear (Esc) starts over."
            : "Paint more strokes (Alt + drag erases), then Remove (Enter) fills the selection; Clear (Esc) starts over.";
    }

    /// <summary>There is a selection to remove (and no fill running).</summary>
    public bool HasRemoveSelection => PendingRemove is not null && !_removing;

    partial void OnPendingRemoveChanged(Spot? value) => RefreshRemoveCommands();

    private void RefreshRemoveCommands()
    {
        OnPropertyChanged(nameof(HasRemoveSelection));
        ApplyRemoveCommand.NotifyCanExecuteChanged();
        ClearRemoveSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Discards the painted selection.</summary>
    [RelayCommand(CanExecute = nameof(HasRemoveSelection))]
    private void ClearRemoveSelection()
    {
        if (_removing)
            return;
        PendingRemove = null;
    }

    /// <summary>Fills the painted selection with the AI model.</summary>
    [RelayCommand(CanExecute = nameof(HasRemoveSelection))]
    private Task ApplyRemove() => PendingRemove is { } selection ? RemoveAsync(selection) : Task.CompletedTask;

    /// <summary>The selection is filled by the model, then the spot is added (one undo step).</summary>
    private async Task RemoveAsync(Spot spot)
    {
        if (Original is not { } original || _removing)
            return;
        _removing = true;
        RefreshRemoveCommands();
        try
        {
            var inpainter = await LoadInpainterAsync();
            Status = "Removing (AI)…";
            // What the photo looks like under the stroke now: corrected, with the earlier spots.
            var photo = _canvasFull is { } canvas && ReferenceEquals(_canvasBase, RetouchBase) && State.Spots.Count > 0
                ? canvas.Bitmap
                : EditBase!;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var fill = await Task.Run(() => inpainter.FillSpot(photo, spot));
            if (!ReferenceEquals(Original, original))
                return;
            spot = spot with { Fill = fill };
            ApplyEdit(State with { Spots = State.Spots.Add(spot) });
            PendingRemove = null;
            SelectedSpotId = null; // ready for the next selection
            Status = $"Removed in {watch.Elapsed.TotalSeconds:0.0} s ({(inpainter.Device == Core.Ai.InferenceDevice.DirectML ? "GPU" : "CPU")}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException
            or System.IO.InvalidDataException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or UnauthorizedAccessException)
        {
            Status = $"Remove failed: {ex.Message}";
        }
        finally
        {
            // On failure the selection stays, so Remove can be tried again.
            _removing = false;
            RefreshRemoveCommands();
        }
    }

    /// <summary>The selection being painted / filled (drawn by the viewer until the spot exists).</summary>
    [ObservableProperty]
    public partial Spot? PendingRemove { get; private set; }

    /// <summary>Stroke points at least a third of the radius apart (the rest adds nothing but work).</summary>
    private static IEnumerable<BrushPoint> SimplifiedPath(IReadOnlyList<BrushPoint> path, float radius)
    {
        var last = path[0];
        yield return last;
        float min = radius / 3;
        for (int i = 1; i < path.Count; i++)
        {
            var p = path[i];
            bool isLast = i == path.Count - 1;
            if (isLast || MathF.Sqrt((p.X - last.X) * (p.X - last.X) + (p.Y - last.Y) * (p.Y - last.Y)) >= min)
            {
                yield return p;
                last = p;
            }
        }
    }

    private Task<Inpainter> LoadInpainterAsync()
    {
        if (_inpainter is not null)
            return Task.FromResult(_inpainter);
        return _inpainterLoading ??= LoadInpainterCoreAsync();
    }

    private async Task<Inpainter> LoadInpainterCoreAsync()
    {
        try
        {
            if (!ModelCatalog.Remove.All(_models.IsAvailable))
            {
                long total = ModelCatalog.Remove.Where(m => !_models.IsAvailable(m)).Sum(m => m.SizeBytes);
                Status = $"Downloading the AI Remove model ({total / 1_000_000} MB, only the first time)…";
                var progress = new Progress<DownloadProgress>(p =>
                    Status = $"Downloading the AI Remove model… {p.Fraction ?? 0:P0} of {total / 1_000_000} MB");
                await _models.GetAllAsync(ModelCatalog.Remove, progress);
            }
            Status = "Loading the AI Remove model…";
            _inpainter = await Task.Run(() => Inpainter.Load(_models.PathOf(ModelCatalog.Inpaint)));
            return _inpainter;
        }
        catch
        {
            _inpainterLoading = null; // allow a retry
            throw;
        }
    }

    // ---- Retouched preview ----

    /// <summary>The shown photo with the spots applied (null = no spots).</summary>
    private PreviewImage? _retouchedPreview;

    /// <summary>What <see cref="_retouchedPreview"/> was made from.</summary>
    private (PreviewImage? Base, object? Spots) _retouchedFrom;

    private bool _retouchRunning;

    /// <summary>The spots changed (edit, undo, another photo): keep the selection valid and rebuild the preview.</summary>
    private void OnSpotsChanged()
    {
        if (SelectedSpotId is { } id && !State.Spots.Exists(s => s.Id == id))
            SelectedSpotId = null;
        RefreshSpotSliders();
        UpdateRetouchedPreview();
    }

    /// <summary>
    /// Rebuilds the retouched preview from the current base (the AI-restored copy or the original) in the
    /// background; while one is being built, the next starts when it is done (a drag = few rebuilds).
    /// </summary>
    private void UpdateRetouchedPreview()
    {
        var basePreview = RetouchBase;
        var spots = State.Spots;
        if (spots.Count == 0 || basePreview is null)
        {
            _retouchedPreview = null;
            _retouchedFrom = default;
            if (!_retouchRunning && !ReferenceEquals(_canvasBase, basePreview))
                (_canvasBase, _canvasFull, _canvasPreview) = (null, null, null); // another photo: let the copies go
            ShowPreview();
            return;
        }
        if (ReferenceEquals(_retouchedFrom.Base, basePreview) && ReferenceEquals(_retouchedFrom.Spots, spots) || _retouchRunning)
            return;
        _retouchRunning = true;
        _ = BuildRetouchedAsync(basePreview, spots);
    }

    // Retouched copies of the shown photo (full size and preview), updated in place; made on first use per base.
    private PreviewImage? _canvasBase;
    private RetouchCanvas? _canvasFull, _canvasPreview;

    private async Task BuildRetouchedAsync(PreviewImage basePreview, System.Collections.Immutable.ImmutableList<Spot> spots)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var retouched = await Task.Run(() =>
            {
                if (!ReferenceEquals(_canvasBase, basePreview))
                {
                    _canvasFull = new RetouchCanvas(basePreview.Full);
                    _canvasPreview = ReferenceEquals(basePreview.Preview, basePreview.Full) ? _canvasFull : new RetouchCanvas(basePreview.Preview);
                    _canvasBase = basePreview;
                }
                _canvasFull!.Update(spots);
                var full = _canvasFull.Snapshot(basePreview.Full);
                if (ReferenceEquals(_canvasPreview, _canvasFull))
                    return PreviewImage.FromImages(full, full);
                _canvasPreview!.Update(spots);
                return PreviewImage.FromImages(full, _canvasPreview.Snapshot(basePreview.Preview));
            });
            Timings.Log($"retouched preview ({spots.Count} spots) in {watch.Elapsed.TotalMilliseconds:0} ms");
            if (ReferenceEquals(basePreview, RetouchBase) && State.Spots.Count > 0)
            {
                _retouchedPreview = retouched;
                _retouchedFrom = (basePreview, spots);
                ShowPreview();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Status = $"Spot removal failed: {ex.Message}";
            _retouchedFrom = (basePreview, spots); // don't retry the same thing
        }
        finally
        {
            _retouchRunning = false;
        }
        // Spots or the base changed meanwhile: build again.
        if (!ReferenceEquals(_retouchedFrom.Spots, State.Spots) || !ReferenceEquals(_retouchedFrom.Base, RetouchBase))
            UpdateRetouchedPreview();
    }
}
