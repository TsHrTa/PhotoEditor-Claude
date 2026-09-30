using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Controls;
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

    /// <summary>Heal (texture from the source, colours fitted) instead of Clone.</summary>
    public bool SpotHeal
    {
        get => (SelectedSpot?.Mode ?? _newSpotMode) == SpotMode.Heal;
        set => ChangeSpot(s => s with { Mode = value ? SpotMode.Heal : SpotMode.Clone }, () => _newSpotMode = value ? SpotMode.Heal : SpotMode.Clone, null);
    }

    public bool SpotClone
    {
        get => !SpotHeal;
        set => SpotHeal = !value;
    }

    /// <summary>Size: radius in % of the photo's longer side.</summary>
    public double SpotSize
    {
        get => Math.Round((SelectedSpot?.Radius ?? _newSpotRadius) * 100, 2);
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
    public double SpotCursorRadius => SelectedSpot?.Radius ?? _newSpotRadius;

    /// <summary>Changes the selected spot (undoable) and the settings for new spots.</summary>
    private void ChangeSpot(Func<Spot, Spot> update, Action setDefault, string? key)
    {
        setDefault();
        if (SelectedSpot is { } spot)
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
        foreach (var name in new[] { nameof(SpotHeal), nameof(SpotClone), nameof(SpotSize), nameof(SpotFeather), nameof(SpotOpacity),
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
            case SpotEditKind.Move or SpotEditKind.MoveSource when id is { } spotId:
                if (phase == EditPhase.Begin)
                {
                    SelectedSpotId = spotId;
                    _spotDragKey = $"spot-drag-{Guid.NewGuid()}"; // one undo step per drag
                }
                ApplyEdit(UpdateSpot(State, spotId, s => kind == SpotEditKind.Move ? s with { Center = point } : s with { Source = point }),
                    _spotDragKey);
                break;
        }
    }

    private string? _spotDragKey;

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
