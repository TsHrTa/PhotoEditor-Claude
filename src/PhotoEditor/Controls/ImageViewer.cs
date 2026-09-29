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
    private CropFrame _displayFrame;

    // Crop frame drag in progress
    private CropHandle _cropHandle;
    private (double X, double Y) _cropFrom;

    // Gradient creation / handle drag in progress
    private GradientHandle _dragHandle;
    private MaskComponent? _dragOriginal;
    private BrushPoint _dragFrom;

    static ImageViewer()
    {
        AffectsRender<ImageViewer>(SourceProperty, StateProperty, OverlayMaskProperty,
            ToolProperty, EditableComponentProperty, BrushRadiusProperty, BrushFeatherProperty);
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
        if (!force && crop == _displayCrop)
            return;
        var old = _displayFrame;
        _displayCrop = crop;
        _displayFrame = crop.Frame(image.Width, image.Height);
        if (force || Math.Abs(old.HalfWidth - _displayFrame.HalfWidth) > 1e-6 || Math.Abs(old.HalfHeight - _displayFrame.HalfHeight) > 1e-6)
            Update(() => _view.SetImageSize(_displayFrame.HalfWidth * 2, _displayFrame.HalfHeight * 2));
    }

    /// <summary>Display (cropped frame) pixel → full-resolution image pixel.</summary>
    private (double X, double Y) DisplayToImage(double x, double y) =>
        _displayFrame.ToImage(x - _displayFrame.HalfWidth, y - _displayFrame.HalfHeight);

    private (double X, double Y) ImageToDisplay(double x, double y)
    {
        var (u, v) = _displayFrame.ToLocal(x, y);
        return (u + _displayFrame.HalfWidth, v + _displayFrame.HalfHeight);
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
        if (Source is null)
            return;
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        var pos = point.Position;
        if (props.IsLeftButtonPressed)
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
        if (Tool == EditTool.Brush)
            InvalidateVisual(); // move the brush cursor
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
        if (Tool == EditTool.Brush)
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
    private double DisplayRotation => -_displayFrame.Angle;

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
        Cursor = null;
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

        if (Tool == EditTool.Brush && _pointer is { } p)
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
