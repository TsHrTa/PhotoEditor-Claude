using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Retouch;
using PhotoEditor.Core.Viewing;
using SkiaSharp;

namespace PhotoEditor.Controls;

/// <summary>
/// Draws the edited image directly on Avalonia's Skia canvas (SkSL adjustment shader), with zoom
/// (fit / 100% / mouse wheel around the cursor), drag-to-pan, brush painting, gradient handles and
/// crop editing.
/// </summary>
/// <remarks>
/// The view shows the cropped result ("display" space = the crop frame, in full-resolution pixels);
/// while the crop tool is active it shows the whole image with the crop frame on top. Mask geometry
/// stays in normalised full-image coordinates and is mapped through the crop.
/// </remarks>
public class ImageViewer : Control
{
    public static readonly StyledProperty<PreviewImage?> SourceProperty =
        AvaloniaProperty.Register<ImageViewer, PreviewImage?>(nameof(Source));

    public static readonly StyledProperty<EditState> StateProperty =
        AvaloniaProperty.Register<ImageViewer, EditState>(nameof(State), EditState.Default);

    public static readonly StyledProperty<Mask?> OverlayMaskProperty =
        AvaloniaProperty.Register<ImageViewer, Mask?>(nameof(OverlayMask));

    public static readonly StyledProperty<EditTool> ToolProperty =
        AvaloniaProperty.Register<ImageViewer, EditTool>(nameof(Tool));

    public static readonly StyledProperty<MaskComponent?> EditableComponentProperty =
        AvaloniaProperty.Register<ImageViewer, MaskComponent?>(nameof(EditableComponent));

    public static readonly StyledProperty<double> BrushRadiusProperty =
        AvaloniaProperty.Register<ImageViewer, double>(nameof(BrushRadius), 0.03);

    public static readonly StyledProperty<double> BrushFeatherProperty =
        AvaloniaProperty.Register<ImageViewer, double>(nameof(BrushFeather), 0.5);

    public static readonly StyledProperty<Guid?> SelectedSpotProperty =
        AvaloniaProperty.Register<ImageViewer, Guid?>(nameof(SelectedSpot));

    public static readonly StyledProperty<double> SpotRadiusProperty =
        AvaloniaProperty.Register<ImageViewer, double>(nameof(SpotRadius), 0.02);

    public static readonly StyledProperty<bool> SpotPaintingProperty =
        AvaloniaProperty.Register<ImageViewer, bool>(nameof(SpotPainting));

    public static readonly StyledProperty<Spot?> PendingSpotProperty =
        AvaloniaProperty.Register<ImageViewer, Spot?>(nameof(PendingSpot));

    public static readonly DirectProperty<ImageViewer, double> ZoomProperty =
        AvaloniaProperty.RegisterDirect<ImageViewer, double>(nameof(Zoom), v => v.Zoom);

    private const double WheelZoomStep = 1.25;
    private const double HandleRadius = 6;
    private const double HandleHitRadius = 10;

    private readonly ViewTransform _view = new();
    private readonly MaskImageCache _maskCache = new();
    private Point? _panStart;

    // Select Object: press position (view), whether Alt was held, current pointer while dragging a box
    private Point? _selectStart;
    private Point? _selectCurrent;
    private bool _selectExclude;
    private const double BoxDragThreshold = 5;
    private Point? _pointer;
    private bool _stroking;
    private double _zoom = 1;

    // Crop shown in the view (Crop.None while the crop tool is active) and its frame in full-res pixels.
    private Crop _displayCrop = Crop.None;
    private PhotoOrientation _displayOrientation;

    /// <summary>The picture is shown mirrored left-right (flip); applied in the frame's local coordinates.</summary>
    private bool _mirror;
    private CropFrame _displayFrame;

    // Crop frame drag in progress
    private CropHandle _cropHandle;
    private (double X, double Y) _cropFrom;

    // Spot / spot source drag in progress: which, and the pointer's offset from the circle's centre (normalised)
    private (SpotEditKind Kind, Guid Id, double Dx, double Dy)? _spotDrag;

    // Remove stroke being painted: its points (normalised) and where the press was (view)
    private List<BrushPoint>? _paint;
    private Point _paintStart;
    private bool _paintErase;

    // Gradient creation / handle drag in progress
    private GradientHandle _dragHandle;
    private MaskComponent? _dragOriginal;
    private BrushPoint _dragFrom;

    static ImageViewer()
    {
        AffectsRender<ImageViewer>(SourceProperty, StateProperty, OverlayMaskProperty,
            ToolProperty, EditableComponentProperty, BrushRadiusProperty, BrushFeatherProperty, SelectedSpotProperty, SpotRadiusProperty,
            SpotPaintingProperty, PendingSpotProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageViewer>(true);
        FocusableProperty.OverrideDefaultValue<ImageViewer>(true);
    }

    /// <summary>The image to show; 100% zoom refers to its full-resolution size.</summary>
    public PreviewImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Edit applied live by the GPU shader.</summary>
    public EditState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Mask shown as a red tint over the image (null = none).</summary>
    public Mask? OverlayMask
    {
        get => GetValue(OverlayMaskProperty);
        set => SetValue(OverlayMaskProperty, value);
    }

    /// <summary>What left-drag does: pan, paint (<see cref="BrushStroke"/>) or create a gradient (<see cref="ComponentEdit"/>).</summary>
    public EditTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>
    /// Hand tool held (Space): left-drag pans whatever <see cref="Tool"/> is, and the tool's cursor is hidden; the
    /// tool resumes when it is released. A stroke or drag already in progress is not interrupted.
    /// </summary>
    public bool HandTool
    {
        get => _handTool;
        set
        {
            if (_handTool == value)
                return;
            _handTool = value;
            if (_panStart is null)
                Cursor = value ? new Cursor(StandardCursorType.Hand) : null;
            InvalidateVisual();
        }
    }
    private bool _handTool;

    /// <summary>Gradient whose on-canvas handles are shown and can be dragged.</summary>
    public MaskComponent? EditableComponent
    {
        get => GetValue(EditableComponentProperty);
        set => SetValue(EditableComponentProperty, value);
    }

    /// <summary>Brush radius as a fraction of the image's longer side (for the cursor).</summary>
    public double BrushRadius
    {
        get => GetValue(BrushRadiusProperty);
        set => SetValue(BrushRadiusProperty, value);
    }

    /// <summary>0..1, drawn as the inner circle of the cursor.</summary>
    public double BrushFeather
    {
        get => GetValue(BrushFeatherProperty);
        set => SetValue(BrushFeatherProperty, value);
    }

    /// <summary>The spot drawn highlighted (spot removal tool).</summary>
    public Guid? SelectedSpot
    {
        get => GetValue(SelectedSpotProperty);
        set => SetValue(SelectedSpotProperty, value);
    }

    /// <summary>Radius of a new spot as a fraction of the image's longer side (for the cursor).</summary>
    public double SpotRadius
    {
        get => GetValue(SpotRadiusProperty);
        set => SetValue(SpotRadiusProperty, value);
    }

    /// <summary>Spot tool paints strokes (AI Remove) instead of adding circles.</summary>
    public bool SpotPainting
    {
        get => GetValue(SpotPaintingProperty);
        set => SetValue(SpotPaintingProperty, value);
    }

    /// <summary>The AI Remove selection being painted / filled (drawn until it becomes a spot).</summary>
    public Spot? PendingSpot
    {
        get => GetValue(PendingSpotProperty);
        set => SetValue(PendingSpotProperty, value);
    }

    /// <summary>Spot removal tool: add a spot, or drag a spot / its source.</summary>
    public event EventHandler<SpotEditEventArgs>? SpotEdit;

    /// <summary>A stroke was painted with the spot tool (normalised points; Alt held at the press = erase).</summary>
    public event EventHandler<SpotPaintedEventArgs>? SpotPainted;

    /// <summary>Brush input in normalised image coordinates (0..1).</summary>
    public event EventHandler<BrushStrokeEventArgs>? BrushStroke;

    /// <summary>A gradient was created or its handles dragged.</summary>
    public event EventHandler<ComponentEditEventArgs>? ComponentEdit;

    /// <summary>The crop frame is dragged (crop tool).</summary>
    public event EventHandler<CropEditEventArgs>? CropEdit;

    /// <summary>Select Object tool: a click (point) or a dragged box.</summary>
    public event EventHandler<ObjectSelectEventArgs>? ObjectSelect;

    /// <summary>Current display scale (1 = 100%).</summary>
    public double Zoom
    {
        get => _zoom;
        private set => SetAndRaise(ZoomProperty, ref _zoom, value);
    }

    public void ZoomToFit() => Update(_view.ZoomToFit);

    public void ZoomToActualSize() => Update(_view.ZoomToActualSize);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            // The render thread may still hold the old image; let the GC release it.
            // Keep zoom and position when the same photo is swapped (Before / After, denoised copy);
            // refit when a photo of another size is opened.
            var previous = change.GetOldValue<PreviewImage?>();
            var next = change.GetNewValue<PreviewImage?>();
            bool sameSize = previous is not null && next is not null && previous.Width == next.Width && previous.Height == next.Height;
            // A RAW's camera preview differs from the decoded RAW by a few pixels: keep the view then too.
            bool nearlySame = previous is not null && next is not null
                && Math.Abs(previous.Width - next.Width) <= previous.Width / 100
                && Math.Abs(previous.Height - next.Height) <= previous.Height / 100;
            if (sameSize)
                InvalidateVisual();
            else if (nearlySame && next is not null)
            {
                SetDisplayFrame(Tool == EditTool.Crop ? Crop.None : State.Crop, State.Orientation, next);
                Update(() => _view.ReplaceImageSize(_displayFrame.HalfWidth * 2, _displayFrame.HalfHeight * 2));
            }
            else
                UpdateDisplayCrop(force: true);
        }
        else if (change.Property == StateProperty || change.Property == ToolProperty)
        {
            UpdateDisplayCrop(force: false);
        }
        else if (change.Property == BoundsProperty)
        {
            Update(() => _view.SetViewSize(Bounds.Width, Bounds.Height));
        }
    }

    /// <summary>Full-resolution image size.</summary>
    private double ImageWidth => Source?.Width ?? 1;
    private double ImageHeight => Source?.Height ?? 1;

    /// <summary>Picks up crop changes; refits the view when the displayed size changes.</summary>
    private void UpdateDisplayCrop(bool force)
    {
        if (Source is not { } image)
            return;
        var crop = Tool == EditTool.Crop ? Crop.None : State.Crop;
        var orientation = State.Orientation;
        if (!force && crop == _displayCrop && orientation == _displayOrientation)
            return;
        var old = _displayFrame;
        bool turned = orientation.SwapsSides != _displayOrientation.SwapsSides;
        SetDisplayFrame(crop, orientation, image);
        if (force || turned || Math.Abs(old.HalfWidth - _displayFrame.HalfWidth) > 1e-6 || Math.Abs(old.HalfHeight - _displayFrame.HalfHeight) > 1e-6)
            Update(() => _view.SetImageSize(_displayFrame.HalfWidth * 2, _displayFrame.HalfHeight * 2));
        else
            InvalidateVisual(); // e.g. flipped: same size, mirrored
    }

    /// <summary>
    /// The displayed frame: the crop frame, turned by the orientation's quarter turns (local axes turned 90°
    /// clockwise per turn, width and height swapped for odd turns); a flip mirrors the local x axis.
    /// </summary>
    private void SetDisplayFrame(Crop crop, PhotoOrientation orientation, PreviewImage image)
    {
        _displayCrop = crop;
        _displayOrientation = orientation;
        var f = crop.Frame(image.Width, image.Height);
        f = f with { Angle = f.Angle - 90 * orientation.Turns };
        if (orientation.SwapsSides)
            f = f with { HalfWidth = f.HalfHeight, HalfHeight = f.HalfWidth };
        _displayFrame = f;
        _mirror = orientation.Flip;
    }

    /// <summary>Display (cropped frame) pixel → full-resolution image pixel.</summary>
    private (double X, double Y) DisplayToImage(double x, double y)
    {
        double u = x - _displayFrame.HalfWidth;
        return _displayFrame.ToImage(_mirror ? -u : u, y - _displayFrame.HalfHeight);
    }

    private (double X, double Y) ImageToDisplay(double x, double y)
    {
        var (u, v) = _displayFrame.ToLocal(x, y);
        return ((_mirror ? -u : u) + _displayFrame.HalfWidth, v + _displayFrame.HalfHeight);
    }

    private void Update(Action change)
    {
        change();
        Zoom = _view.Scale;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null)
            return;
        var p = e.GetPosition(this);
        double factor = Math.Pow(WheelZoomStep, e.Delta.Y);
        Update(() => _view.ZoomBy(factor, p.X, p.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(); // so keys (← / → between photos) are not left with a text box elsewhere
        if (Source is null)
            return;
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        var pos = point.Position;
        if (props.IsLeftButtonPressed && !HandTool)
        {
            if (Tool == EditTool.Crop && HitTestCrop(pos) is var cropHandle and not CropHandle.None)
            {
                _cropHandle = cropHandle;
                _cropFrom = ToNormalizedPoint(pos);
                e.Pointer.Capture(this);
                e.Handled = true;
                CropEdit?.Invoke(this, new CropEditEventArgs(cropHandle, _cropFrom, _cropFrom, EditPhase.Begin));
                return;
            }
            if (Tool == EditTool.ObjectSelect)
            {
                _selectStart = _selectCurrent = pos;
                _selectExclude = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (Tool == EditTool.Spot)
            {
                PressSpot(e, pos);
                return;
            }
            if (Tool == EditTool.Brush)
            {
                _stroking = true;
                e.Pointer.Capture(this);
                RaiseStroke(BrushStrokePhase.Begin, pos, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                e.Handled = true;
                return;
            }
            if (Tool != EditTool.Crop && EditableComponent is { } editable && HitTestHandle(editable, pos) is var handle and not GradientHandle.None)
            {
                BeginDrag(e, editable, handle, isNew: false);
                return;
            }
            if (Tool is EditTool.LinearGradient or EditTool.RadialGradient)
            {
                var p = ToNormalized(pos);
                MaskComponent created = Tool == EditTool.LinearGradient
                    ? new LinearGradientComponent { Start = p, End = p }
                    : new RadialGradientComponent { Center = p, RadiusX = 0, RadiusY = 0 };
                BeginDrag(e, created, GradientHandle.End, isNew: true);
                return;
            }
            if (e.ClickCount == 2)
            {
                // Double-click toggles between fit and 100% at the clicked point.
                if (_view.IsFit)
                    Update(() => _view.ZoomTo(1, pos.X, pos.Y));
                else
                    Update(_view.ZoomToFit);
                e.Handled = true;
                return;
            }
        }
        if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            _panStart = pos;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    private void BeginDrag(PointerPressedEventArgs e, MaskComponent component, GradientHandle handle, bool isNew)
    {
        _dragHandle = handle;
        _dragOriginal = component;
        _dragFrom = ToNormalized(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
        ComponentEdit?.Invoke(this, new ComponentEditEventArgs(component, EditPhase.Begin, isNew));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        if (_cropHandle != CropHandle.None)
        {
            CropEdit?.Invoke(this, new CropEditEventArgs(_cropHandle, _cropFrom, ToNormalizedPoint(p), EditPhase.Move));
        }
        else if (_selectStart is not null)
        {
            _selectCurrent = p;
            InvalidateVisual(); // rubber band
        }
        else if (_stroking)
        {
            RaiseStroke(BrushStrokePhase.Move, p, false);
        }
        else if (_spotDrag is { } drag)
        {
            SpotEdit?.Invoke(this, new SpotEditEventArgs(drag.Kind, drag.Id, SpotDragPoint(drag, p), EditPhase.Move));
        }
        else if (_paint is { } paint)
        {
            paint.Add(ToNormalized(p));
            InvalidateVisual();
        }
        else if (_dragOriginal is { } original)
        {
            ComponentEdit?.Invoke(this, new ComponentEditEventArgs(Dragged(original, p), EditPhase.Move, false));
        }
        else if (_panStart is { } start)
        {
            _panStart = p;
            Update(() => _view.Pan(p.X - start.X, p.Y - start.Y));
            return;
        }
        else if (HandTool)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }
        else if (Tool == EditTool.Crop)
        {
            Cursor = CropCursor(HitTestCrop(p));
        }
        else if (Tool == EditTool.ObjectSelect)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
        }
        else if (EditableComponent is { } editable)
        {
            Cursor = HitTestHandle(editable, p) != GradientHandle.None ? new Cursor(StandardCursorType.Hand) : null;
        }
        if (Tool is EditTool.Brush or EditTool.Spot)
            InvalidateVisual(); // move the brush / spot cursor
    }

    private MaskComponent Dragged(MaskComponent original, Point viewPoint)
    {
        var to = ToNormalized(viewPoint);
        return original switch
        {
            LinearGradientComponent linear => linear.DragHandle(_dragHandle, _dragFrom, to),
            RadialGradientComponent radial => radial.DragHandle(_dragHandle, _dragFrom, to, Aspect),
            _ => original,
        };
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        if (Tool is EditTool.Brush or EditTool.Spot)
            InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selectStart is { } selectStart)
        {
            var end = e.GetPosition(this);
            _selectStart = _selectCurrent = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
            if (Point.Distance(selectStart, end) >= BoxDragThreshold)
            {
                // Box: the view rectangle's corners in image coordinates (approximate on a straightened crop).
                var a = ToNormalizedPoint(selectStart);
                var b = ToNormalizedPoint(end);
                var box = new SelectBox(
                    (float)Math.Clamp(Math.Min(a.X, b.X), 0, 1), (float)Math.Clamp(Math.Min(a.Y, b.Y), 0, 1),
                    (float)Math.Clamp(Math.Max(a.X, b.X), 0, 1), (float)Math.Clamp(Math.Max(a.Y, b.Y), 0, 1));
                ObjectSelect?.Invoke(this, new ObjectSelectEventArgs(null, box));
            }
            else
            {
                var (x, y) = ToNormalizedPoint(end);
                if (x is >= 0 and <= 1 && y is >= 0 and <= 1)
                    ObjectSelect?.Invoke(this, new ObjectSelectEventArgs(new SelectPoint((float)x, (float)y, !_selectExclude), null));
            }
            return;
        }
        if (_cropHandle != CropHandle.None)
        {
            var handle = _cropHandle;
            _cropHandle = CropHandle.None;
            e.Pointer.Capture(null);
            CropEdit?.Invoke(this, new CropEditEventArgs(handle, _cropFrom, ToNormalizedPoint(e.GetPosition(this)), EditPhase.End));
            return;
        }
        if (_stroking)
        {
            _stroking = false;
            e.Pointer.Capture(null);
            RaiseStroke(BrushStrokePhase.End, e.GetPosition(this), false);
            return;
        }
        if (_spotDrag is { } drag)
        {
            _spotDrag = null;
            e.Pointer.Capture(null);
            SpotEdit?.Invoke(this, new SpotEditEventArgs(drag.Kind, drag.Id, SpotDragPoint(drag, e.GetPosition(this)), EditPhase.End));
            return;
        }
        if (_paint is { } painted)
        {
            _paint = null;
            e.Pointer.Capture(null);
            var end = e.GetPosition(this);
            // A click (no drag): on a spot selects it; elsewhere adds a circle (heal / clone) or a dot (remove).
            // A drag paints a stroke.
            bool click = Point.Distance(_paintStart, end) < BoxDragThreshold;
            if (click && HitSpot(end) is { } hit)
                SpotEdit?.Invoke(this, new SpotEditEventArgs(SpotEditKind.Select, hit.Id, hit.Center, EditPhase.End));
            else if (click && !SpotPainting)
            {
                var point = painted[0];
                if (point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1)
                    SpotEdit?.Invoke(this, new SpotEditEventArgs(SpotEditKind.Add, null, point, EditPhase.End));
            }
            else
                SpotPainted?.Invoke(this, new SpotPaintedEventArgs(click ? [painted[0]] : painted, _paintErase));
            InvalidateVisual();
            return;
        }
        if (_dragOriginal is { } original)
        {
            var final = Dragged(original, e.GetPosition(this));
            _dragOriginal = null;
            e.Pointer.Capture(null);
            ComponentEdit?.Invoke(this, new ComponentEditEventArgs(final, EditPhase.End, false));
            return;
        }
        EndPan(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_cropHandle != CropHandle.None)
        {
            var handle = _cropHandle;
            _cropHandle = CropHandle.None;
            CropEdit?.Invoke(this, new CropEditEventArgs(handle, _cropFrom, _cropFrom, EditPhase.End));
        }
        if (_stroking)
        {
            _stroking = false;
            BrushStroke?.Invoke(this, new BrushStrokeEventArgs(BrushStrokePhase.End, 0, 0, false));
        }
        if (_dragOriginal is not null)
        {
            _dragOriginal = null;
            ComponentEdit?.Invoke(this, new ComponentEditEventArgs(null, EditPhase.End, false));
        }
        if (_spotDrag is { } drag)
        {
            _spotDrag = null;
            SpotEdit?.Invoke(this, new SpotEditEventArgs(drag.Kind, drag.Id, SpotCenter(drag), EditPhase.End));
        }
        _paint = null;
        _panStart = null;
        _selectStart = _selectCurrent = null;
        Cursor = null;
    }

    /// <summary>Image width / height.</summary>
    private float Aspect => (float)(ImageWidth / ImageHeight);

    /// <summary>View point → normalised full-image coordinates.</summary>
    private (double X, double Y) ToNormalizedPoint(Point viewPoint)
    {
        var (dx, dy) = _view.ViewToImage(viewPoint.X, viewPoint.Y);
        var (ix, iy) = DisplayToImage(dx, dy);
        return (ix / ImageWidth, iy / ImageHeight);
    }

    private BrushPoint ToNormalized(Point viewPoint)
    {
        var (x, y) = ToNormalizedPoint(viewPoint);
        return new BrushPoint((float)x, (float)y);
    }

    /// <summary>Full-resolution image pixel → view point.</summary>
    private Point ImagePixelToView(double x, double y)
    {
        var (dx, dy) = ImageToDisplay(x, y);
        var (vx, vy) = _view.ImageToView(dx, dy);
        return new Point(vx, vy);
    }

    private Point ToView(BrushPoint p) => ImagePixelToView(p.X * ImageWidth, p.Y * ImageHeight);

    /// <summary>Degrees by which image axes appear rotated in the view (the crop's straighten angle, reversed).</summary>
    private double DisplayRotation => _mirror ? _displayFrame.Angle : -_displayFrame.Angle;

    // ---- Spot removal tool ----

    /// <summary>Radius on screen of a circle with <paramref name="radius"/> (fraction of the longer side).</summary>
    private double ViewRadius(double radius) => radius * Math.Max(ImageWidth, ImageHeight) * _view.Scale;

    /// <summary>
    /// A press with the spot tool: on the selected spot's source circle drags the source, on a spot drags the spot
    /// (the selected spot is tested first, then the newest), elsewhere adds a spot.
    /// </summary>
    private void PressSpot(PointerPressedEventArgs e, Point pos)
    {
        e.Handled = true;
        if (SpotPainting)
        {
            _paintErase = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            _paint = [ToNormalized(pos)];
            _paintStart = pos;
            e.Pointer.Capture(this);
            return;
        }
        var spots = State.Spots;
        var order = spots.Reverse().OrderByDescending(s => s.Id == SelectedSpot).ToList();
        foreach (var kind in new[] { SpotEditKind.MoveSource, SpotEditKind.Move })
        {
            foreach (var spot in order)
            {
                if (spot.Mode == SpotMode.Remove)
                    continue; // painted: selected by a click, not dragged
                if (kind == SpotEditKind.MoveSource && spot.Id != SelectedSpot)
                    continue; // only the selected spot shows its source
                var center = kind == SpotEditKind.Move ? spot.Center : spot.Source;
                if (!Hits(spot, kind == SpotEditKind.MoveSource, pos))
                    continue;
                var p = ToNormalized(pos);
                var drag = (kind, spot.Id, (double)center.X - p.X, (double)center.Y - p.Y);
                _spotDrag = drag;
                e.Pointer.Capture(this);
                SpotEdit?.Invoke(this, new SpotEditEventArgs(kind, spot.Id, center, EditPhase.Begin));
                return;
            }
        }
        if (HitSpot(pos) is { Mode: SpotMode.Remove } removed)
        {
            SpotEdit?.Invoke(this, new SpotEditEventArgs(SpotEditKind.Select, removed.Id, removed.Center, EditPhase.End));
            return;
        }
        // Empty photo: a click adds a circle, a drag paints a stroke (decided on release).
        _paintErase = false;
        _paint = [ToNormalized(pos)];
        _paintStart = pos;
        e.Pointer.Capture(this);
    }

    /// <summary>A view point on a spot (its circle or stroke), or with <paramref name="atSource"/> on its source.</summary>
    private bool Hits(Spot spot, bool atSource, Point pos)
    {
        if (spot.Mode == SpotMode.Remove)
            return spot.RemoveStrokes.Any(s => !s.Erase
                && s.Path.Any(p => Point.Distance(ToView(p), pos) <= Math.Max(ViewRadius(s.Radius), HandleHitRadius)));
        double r = Math.Max(ViewRadius(spot.Radius), HandleHitRadius);
        var points = spot.Path.Count > 1 ? spot.Path : [spot.Center];
        float dx = atSource ? spot.Source.X - spot.Center.X : 0, dy = atSource ? spot.Source.Y - spot.Center.Y : 0;
        return points.Any(p => Point.Distance(ToView(new BrushPoint(p.X + dx, p.Y + dy)), pos) <= r);
    }

    /// <summary>The newest spot under a view point (a circle, or a Remove stroke), or null.</summary>
    private Spot? HitSpot(Point pos)
    {
        foreach (var spot in State.Spots.Reverse())
        {
            double r = Math.Max(ViewRadius(spot.Radius), HandleHitRadius);
            if (Hits(spot, false, pos))
                return spot;
        }
        return null;
    }

    private BrushPoint SpotDragPoint((SpotEditKind Kind, Guid Id, double Dx, double Dy) drag, Point viewPoint)
    {
        var p = ToNormalized(viewPoint);
        return new BrushPoint((float)Math.Clamp(p.X + drag.Dx, 0, 1), (float)Math.Clamp(p.Y + drag.Dy, 0, 1));
    }

    private BrushPoint SpotCenter((SpotEditKind Kind, Guid Id, double Dx, double Dy) drag) =>
        State.Spots.Find(s => s.Id == drag.Id) is { } spot ? (drag.Kind == SpotEditKind.Move ? spot.Center : spot.Source) : default;

    private static readonly IPen SpotSelected = new Pen(Brushes.White, 2);

    /// <summary>A painted stroke as a band of its brush width.</summary>
    private void DrawStroke(DrawingContext context, IReadOnlyList<BrushPoint> path, double radius, Color color)
    {
        if (path.Count == 0)
            return;
        double r = ViewRadius(radius);
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, 2 * r, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (path.Count == 1)
        {
            context.DrawEllipse(brush, null, ToView(path[0]), r, r);
            return;
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToView(path[0]), false);
            foreach (var p in path.Skip(1))
                g.LineTo(ToView(p));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>A Remove selection's outline in image pixels (painted strokes minus erased ones, in order), made once per spot.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Spot, Tuple<double, double, Geometry?>> SelectionShapes = new();

    private Geometry? SelectionShape(Spot spot)
    {
        if (!SelectionShapes.TryGetValue(spot, out var cached) || cached.Item1 != ImageWidth || cached.Item2 != ImageHeight)
        {
            cached = Tuple.Create(ImageWidth, ImageHeight, BuildSelectionShape(spot));
            SelectionShapes.AddOrUpdate(spot, cached);
        }
        return cached.Item3;
    }

    private Geometry? BuildSelectionShape(Spot spot)
    {
        double longSide = Math.Max(ImageWidth, ImageHeight);
        Geometry? shape = null;
        foreach (var stroke in spot.RemoveStrokes)
        {
            double r = stroke.Radius * longSide;
            var points = stroke.Path.Select(p => new Point(p.X * ImageWidth, p.Y * ImageHeight)).ToList();
            Geometry g;
            if (points.Count == 1)
                g = new EllipseGeometry(new Rect(points[0].X - r, points[0].Y - r, 2 * r, 2 * r));
            else
            {
                var line = new StreamGeometry();
                using (var c = line.Open())
                {
                    c.BeginFigure(points[0], false);
                    foreach (var p in points.Skip(1))
                        c.LineTo(p);
                    c.EndFigure(false);
                }
                g = line.GetWidenedGeometry(new Pen(Brushes.Black, 2 * r, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
            }
            shape = shape is null
                ? (stroke.Erase ? null : g)
                : new CombinedGeometry(stroke.Erase ? GeometryCombineMode.Exclude : GeometryCombineMode.Union, shape, g);
        }
        return shape;
    }

    /// <summary>A Remove selection filled with <paramref name="color"/>.</summary>
    private void DrawSelection(DrawingContext context, Spot spot, Color color)
    {
        if (SelectionShape(spot) is not { } shape)
            return;
        // Image pixel → view is affine (zoom, pan, straighten, mirror).
        var o = ImagePixelToView(0, 0);
        var ex = ImagePixelToView(1, 0) - o;
        var ey = ImagePixelToView(0, 1) - o;
        using (context.PushTransform(new Matrix(ex.X, ex.Y, ey.X, ey.Y, o.X, o.Y)))
            context.DrawGeometry(new SolidColorBrush(color), null, shape);
    }

    /// <summary>Each spot: its circle (solid), its source (dashed) and a line from the source to the spot.</summary>
    private void DrawSpots(DrawingContext context)
    {
        if (PendingSpot is { } pending)
            DrawSelection(context, pending, Color.FromArgb(110, 255, 80, 80));
        if (_paint is { } painting)
            DrawStroke(context, painting, SpotRadius, _paintErase ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255));
        foreach (var spot in State.Spots)
        {
            if (spot.Path.Count > 1 && spot.Mode != SpotMode.Remove)
            {
                // Painted heal / clone: selected shows the stroke, its source and the link; otherwise a ring.
                var at = ToView(spot.Center);
                if (spot.Id == SelectedSpot)
                {
                    float sdx = spot.Source.X - spot.Center.X, sdy = spot.Source.Y - spot.Center.Y;
                    DrawStroke(context, spot.Path, spot.Radius, Color.FromArgb(90, 255, 255, 255));
                    DrawStroke(context, spot.Path.Select(p => new BrushPoint(p.X + sdx, p.Y + sdy)).ToList(), spot.Radius,
                        Color.FromArgb(70, 120, 190, 255));
                    context.DrawLine(GuideShadow, at, ToView(spot.Source));
                    context.DrawLine(GuideDashed, at, ToView(spot.Source));
                }
                context.DrawEllipse(null, GuideShadow, at, 5, 5);
                context.DrawEllipse(null, spot.Id == SelectedSpot ? SpotSelected : GuideLine, at, 5, 5);
                continue;
            }
            if (spot.Mode == SpotMode.Remove)
            {
                // Selected: the stroke(s) as a band; otherwise a small ring where it is.
                if (spot.Id == SelectedSpot)
                    DrawSelection(context, spot, Color.FromArgb(90, 255, 255, 255));
                var at = ToView(spot.Center);
                context.DrawEllipse(null, GuideShadow, at, 5, 5);
                context.DrawEllipse(null, spot.Id == SelectedSpot ? SpotSelected : GuideLine, at, 5, 5);
                continue;
            }
            bool selected = spot.Id == SelectedSpot;
            var c = ToView(spot.Center);
            var s = ToView(spot.Source);
            double r = ViewRadius(spot.Radius);
            var d = c - s;
            double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (selected && length > 2 * r)
            {
                var u = d / length;
                context.DrawLine(GuideShadow, s + u * r, c - u * r);
                context.DrawLine(GuideLine, s + u * r, c - u * r);
            }
            context.DrawEllipse(null, GuideShadow, c, r, r);
            context.DrawEllipse(null, selected ? SpotSelected : GuideLine, c, r, r);
            if (selected)
            {
                context.DrawEllipse(null, GuideShadow, s, r, r);
                context.DrawEllipse(null, GuideDashed, s, r, r);
            }
        }
    }

    // ---- Crop tool ----

    /// <summary>The crop frame being edited, in full-resolution pixels.</summary>
    private CropFrame EditedCropFrame => State.Crop.Frame(ImageWidth, ImageHeight);

    private IEnumerable<(CropHandle Handle, Point Position)> CropHandles()
    {
        var f = EditedCropFrame;
        double w = f.HalfWidth, h = f.HalfHeight;
        foreach (var (handle, u, v) in new[]
        {
            (CropHandle.TopLeft, -w, -h), (CropHandle.Top, 0, -h), (CropHandle.TopRight, w, -h),
            (CropHandle.Right, w, 0), (CropHandle.BottomRight, w, h), (CropHandle.Bottom, 0, h),
            (CropHandle.BottomLeft, -w, h), (CropHandle.Left, -w, 0),
        })
        {
            var (x, y) = f.ToImage(u, v);
            yield return (handle, ImagePixelToView(x, y));
        }
    }

    private CropHandle HitTestCrop(Point viewPoint)
    {
        var best = CropHandle.None;
        double bestDistance = HandleHitRadius;
        foreach (var (handle, pos) in CropHandles())
        {
            double d = Point.Distance(pos, viewPoint);
            if (d <= bestDistance)
            {
                best = handle;
                bestDistance = d;
            }
        }
        if (best != CropHandle.None)
            return best;
        var (nx, ny) = ToNormalizedPoint(viewPoint);
        var f = EditedCropFrame;
        var (u, v) = f.ToLocal(nx * ImageWidth, ny * ImageHeight);
        return Math.Abs(u) <= f.HalfWidth && Math.Abs(v) <= f.HalfHeight ? CropHandle.Move : CropHandle.None;
    }

    private Cursor? CropCursor(CropHandle handle) => handle switch
    {
        CropHandle.Move => new Cursor(StandardCursorType.SizeAll),
        CropHandle.Left or CropHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
        CropHandle.Top or CropHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
        CropHandle.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
        CropHandle.TopRight => new Cursor(StandardCursorType.TopRightCorner),
        CropHandle.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
        CropHandle.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
        _ => null,
    };

    private static readonly IBrush CropShade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IPen CropGrid = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);

    /// <summary>Darkens everything outside the crop frame; draws its outline, a rule-of-thirds grid and handles.</summary>
    private void DrawCropFrame(DrawingContext context, Rect bounds)
    {
        var f = EditedCropFrame;
        var corners = f.Corners().Select(c => ImagePixelToView(c.X, c.Y)).ToArray();
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            g.BeginFigure(bounds.TopLeft, true);
            g.LineTo(bounds.TopRight);
            g.LineTo(bounds.BottomRight);
            g.LineTo(bounds.BottomLeft);
            g.EndFigure(true);
            g.BeginFigure(corners[0], true);
            for (int i = 1; i < 4; i++)
                g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        context.DrawGeometry(CropShade, null, geometry);

        for (int i = 1; i <= 2; i++)
        {
            double t = i / 3.0 * 2 - 1; // -1/3, +1/3 of the full width
            var (ax, ay) = f.ToImage(t * f.HalfWidth, -f.HalfHeight);
            var (bx, by) = f.ToImage(t * f.HalfWidth, f.HalfHeight);
            context.DrawLine(CropGrid, ImagePixelToView(ax, ay), ImagePixelToView(bx, by));
            (ax, ay) = f.ToImage(-f.HalfWidth, t * f.HalfHeight);
            (bx, by) = f.ToImage(f.HalfWidth, t * f.HalfHeight);
            context.DrawLine(CropGrid, ImagePixelToView(ax, ay), ImagePixelToView(bx, by));
        }
        for (int i = 0; i < 4; i++)
        {
            context.DrawLine(GuideShadow, corners[i], corners[(i + 1) % 4]);
            context.DrawLine(GuideLine, corners[i], corners[(i + 1) % 4]);
        }
        foreach (var (_, pos) in CropHandles())
        {
            var r = new Rect(pos.X - 4, pos.Y - 4, 8, 8);
            context.DrawRectangle(Brushes.White, GuideShadow, r);
        }
    }

    private void RaiseStroke(BrushStrokePhase phase, Point viewPoint, bool erase)
    {
        var p = ToNormalized(viewPoint);
        BrushStroke?.Invoke(this, new BrushStrokeEventArgs(phase, p.X, p.Y, erase));
    }

    private void EndPan(IPointer pointer)
    {
        if (_panStart is null)
            return;
        _panStart = null;
        pointer.Capture(null);
        Cursor = HandTool ? new Cursor(StandardCursorType.Hand) : null;
    }

    /// <summary>View positions of the draggable handles of a gradient.</summary>
    private IEnumerable<(GradientHandle Handle, Point Position)> Handles(MaskComponent component) => component switch
    {
        LinearGradientComponent g => [(GradientHandle.Start, ToView(g.Start)), (GradientHandle.End, ToView(g.End)), (GradientHandle.Move, ToView(g.Center))],
        RadialGradientComponent g => g.HandlePoints(Aspect).Select(h => (h.Handle, ToView(h.Point))),
        _ => [],
    };

    private GradientHandle HitTestHandle(MaskComponent component, Point viewPoint)
    {
        var best = GradientHandle.None;
        double bestDistance = HandleHitRadius;
        foreach (var (handle, pos) in Handles(component))
        {
            double d = Point.Distance(pos, viewPoint);
            if (d <= bestDistance)
            {
                best = handle;
                bestDistance = d;
            }
        }
        return best;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // makes the whole area hit-testable
        if (Source is not { } source || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var (x0, y0) = _view.ImageToView(0, 0);
        var (x1, y1) = _view.ImageToView(_view.ImageWidth, _view.ImageHeight);
        var clip = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        // Use the full-resolution image only when the preview would be magnified.
        var image = _view.Scale > source.PreviewScale * 1.01 ? source.Full : source.Preview;
        // Drawn image pixel → view: scale to full resolution, into the crop frame, then the view transform.
        var f = _displayFrame;
        var toView = SKMatrix.CreateTranslation((float)_view.OffsetX, (float)_view.OffsetY)
            .PreConcat(SKMatrix.CreateScale((float)_view.Scale, (float)_view.Scale))
            .PreConcat(SKMatrix.CreateTranslation((float)f.HalfWidth, (float)f.HalfHeight))
            .PreConcat(SKMatrix.CreateScale(_mirror ? -1f : 1f, 1f))
            .PreConcat(SKMatrix.CreateRotationDegrees((float)-f.Angle))
            .PreConcat(SKMatrix.CreateTranslation((float)-f.CenterX, (float)-f.CenterY))
            .PreConcat(SKMatrix.CreateScale((float)(ImageWidth / image.Width), (float)(ImageHeight / image.Height)));
        // Masks are rasterised at preview resolution on the UI thread (cached until they change).
        var state = State;
        var (mw, mh) = MaskImageCache.MaskSize(source.Preview.Width, source.Preview.Height);
        var maskImages = state.Masks.Where(m => m.IsActive)
            .ToDictionary(m => m.Id, m => _maskCache.Get(m, mw, mh));
        var overlay = OverlayMask is { } om ? _maskCache.Get(om, mw, mh) : null;
        _maskCache.Retain(OverlayMask is { } keep ? state.Masks.Add(keep) : state.Masks);
        context.Custom(new ImageDrawOperation(bounds, image, clip, toView, _view.Scale, (double)image.Width / source.Width,
            state, maskImages, overlay));

        if (Tool == EditTool.Crop)
            DrawCropFrame(context, bounds);
        else if (EditableComponent is { } editable)
            DrawGradientGuides(context, editable);

        if (_selectStart is { } s0 && _selectCurrent is { } s1 && Point.Distance(s0, s1) >= BoxDragThreshold)
        {
            var box = new Rect(s0, s1);
            context.DrawRectangle(null, GuideShadow, box);
            context.DrawRectangle(null, GuideDashed, box);
        }

        if (Tool == EditTool.Spot)
        {
            DrawSpots(context);
            if (_pointer is { } sp && _spotDrag is null && _paint is null && !HandTool)
            {
                double r = ViewRadius(SpotRadius);
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), sp, r, r);
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1), sp, r, r);
            }
        }

        if (Tool == EditTool.Brush && _pointer is { } p && !HandTool)
        {
            // Brush cursor: outer circle = radius, inner = where the feather starts.
            double r = BrushRadius * Math.Max(ImageWidth, ImageHeight) * _view.Scale;
            var dark = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3);
            var light = new Pen(Brushes.White, 1);
            context.DrawEllipse(null, dark, p, r, r);
            context.DrawEllipse(null, light, p, r, r);
            double inner = r * (1 - Math.Clamp(BrushFeather, 0, 1));
            if (inner > 1 && inner < r - 1)
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1), p, inner, inner);
        }
    }

    private static readonly IPen GuideShadow = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3);
    private static readonly IPen GuideLine = new Pen(Brushes.White, 1);
    private static readonly IPen GuideDashed = new Pen(Brushes.White, 1, new DashStyle([4, 4], 0));

    /// <summary>Lines/ellipses showing the gradient plus its handles.</summary>
    private void DrawGradientGuides(DrawingContext context, MaskComponent component)
    {
        if (component is LinearGradientComponent linear)
        {
            // Three lines perpendicular to the gradient direction: start (full), centre, end (none).
            var a = ToView(linear.Start);
            var b = ToView(linear.End);
            var d = b - a;
            double length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (length > 0.5)
            {
                var n = new Vector(-d.Y / length, d.X / length) * (Bounds.Width + Bounds.Height);
                var c = ToView(linear.Center);
                foreach (var (p, pen) in new[] { (a, GuideLine), (c, GuideDashed), (b, GuideLine) })
                {
                    context.DrawLine(GuideShadow, p - n, p + n);
                    context.DrawLine(pen, p - n, p + n);
                }
            }
        }

        else if (component is RadialGradientComponent radial)
        {
            // Outer ellipse = edge of the effect, dashed = where the feather starts.
            var c = ToView(radial.Center);
            double rx = radial.RadiusX * ImageWidth * _view.Scale;
            double ry = radial.RadiusY * ImageWidth * _view.Scale;
            double f = 1 - Math.Clamp(radial.Feather, 0, 1);
            // The ellipse is axis-aligned in the image, which appears rotated when the crop is straightened.
            using (context.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y)
                * Matrix.CreateRotation(DisplayRotation * Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y)))
            {
                context.DrawEllipse(null, GuideShadow, c, rx, ry);
                context.DrawEllipse(null, GuideLine, c, rx, ry);
                if (f > 0.01 && f < 0.99)
                    context.DrawEllipse(null, GuideDashed, c, rx * f, ry * f);
            }
        }

        else if (component is RasterMaskComponent raster)
        {
            // The clicks (green = include, red = exclude) and the box of an AI selection.
            if (raster.Box is { } b)
            {
                var r = new Rect(ToView(new BrushPoint(b.Left, b.Top)), ToView(new BrushPoint(b.Right, b.Bottom)));
                context.DrawRectangle(null, GuideShadow, r);
                context.DrawRectangle(null, GuideDashed, r);
            }
            foreach (var point in raster.Points)
            {
                var brush = point.Include ? IncludeBrush : ExcludeBrush;
                context.DrawEllipse(brush, GuideShadow, ToView(new BrushPoint(point.X, point.Y)), 5, 5);
            }
        }

        foreach (var (_, pos) in Handles(component))
        {
            context.DrawEllipse(Brushes.White, GuideShadow, pos, HandleRadius, HandleRadius);
        }
    }

    private static readonly IBrush IncludeBrush = new SolidColorBrush(Color.FromRgb(60, 200, 90));
    private static readonly IBrush ExcludeBrush = new SolidColorBrush(Color.FromRgb(230, 60, 60));

    private sealed class ImageDrawOperation(
        Rect bounds, SKImage image, SKRect clip, SKMatrix toView, double scale, double pixelScale, EditState state,
        Dictionary<Guid, SKImage> maskImages, SKImage? overlay)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
                return;
            using var lease = leaseFeature.Lease();
            // Smooth when shrinking, crisp pixels when zoomed in far.
            var sampling = scale >= 2
                ? new SKSamplingOptions(SKFilterMode.Nearest)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            var canvas = lease.SkCanvas;
            using var shader = AdjustmentShader.CreateShader(image, state, sampling, m => maskImages.GetValueOrDefault(m.Id), pixelScale);
            using var paint = new SKPaint { Shader = shader };
            canvas.Save();
            canvas.ClipRect(clip, antialias: true);
            canvas.Concat(in toView);
            canvas.DrawRect(0, 0, image.Width, image.Height, paint);
            if (overlay is not null)
            {
                using var overlayShader = MaskOverlay.CreateShader(overlay, image.Width, image.Height);
                using var overlayPaint = new SKPaint { Shader = overlayShader };
                canvas.DrawRect(0, 0, image.Width, image.Height, overlayPaint);
            }
            canvas.Restore();
        }
    }
}
