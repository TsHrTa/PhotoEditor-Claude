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
/// (fit / 100% / mouse wheel around the cursor), drag-to-pan, brush painting and gradient handles.
/// </summary>
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
    private Point? _pointer;
    private bool _stroking;
    private double _zoom = 1;

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
            if (change.GetNewValue<PreviewImage?>() is { } image)
                Update(() => _view.SetImageSize(image.Width, image.Height));
        }
        else if (change.Property == BoundsProperty)
        {
            Update(() => _view.SetViewSize(Bounds.Width, Bounds.Height));
        }
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
            if (Tool == EditTool.Brush)
            {
                _stroking = true;
                e.Pointer.Capture(this);
                RaiseStroke(BrushStrokePhase.Begin, pos, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                e.Handled = true;
                return;
            }
            if (EditableComponent is { } editable && HitTestHandle(editable, pos) is var handle and not GradientHandle.None)
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
        if (_stroking)
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
        Cursor = null;
    }

    /// <summary>Image width / height.</summary>
    private float Aspect => _view.ImageHeight > 0 ? (float)(_view.ImageWidth / _view.ImageHeight) : 1f;

    private BrushPoint ToNormalized(Point viewPoint)
    {
        var (ix, iy) = _view.ViewToImage(viewPoint.X, viewPoint.Y);
        return new BrushPoint((float)(ix / _view.ImageWidth), (float)(iy / _view.ImageHeight));
    }

    private Point ToView(BrushPoint p)
    {
        var (x, y) = _view.ImageToView(p.X * _view.ImageWidth, p.Y * _view.ImageHeight);
        return new Point(x, y);
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
        var dest = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        // Use the full-resolution image only when the preview would be magnified.
        var image = _view.Scale > source.PreviewScale * 1.01 ? source.Full : source.Preview;
        // Masks are rasterised at preview resolution on the UI thread (cached until they change).
        var state = State;
        var (mw, mh) = MaskImageCache.MaskSize(source.Preview.Width, source.Preview.Height);
        var maskImages = state.Masks.Where(m => m.IsActive)
            .ToDictionary(m => m.Id, m => _maskCache.Get(m, mw, mh));
        var overlay = OverlayMask is { } om ? _maskCache.Get(om, mw, mh) : null;
        _maskCache.Retain(OverlayMask is { } keep ? state.Masks.Add(keep) : state.Masks);
        context.Custom(new ImageDrawOperation(bounds, image, dest, _view.Scale, state, maskImages, overlay));

        if (EditableComponent is { } editable)
            DrawGradientGuides(context, editable);

        if (Tool == EditTool.Brush && _pointer is { } p)
        {
            // Brush cursor: outer circle = radius, inner = where the feather starts.
            double r = BrushRadius * Math.Max(_view.ImageWidth, _view.ImageHeight) * _view.Scale;
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
            double rx = radial.RadiusX * _view.ImageWidth * _view.Scale;
            double ry = radial.RadiusY * _view.ImageWidth * _view.Scale;
            double f = 1 - Math.Clamp(radial.Feather, 0, 1);
            context.DrawEllipse(null, GuideShadow, c, rx, ry);
            context.DrawEllipse(null, GuideLine, c, rx, ry);
            if (f > 0.01 && f < 0.99)
                context.DrawEllipse(null, GuideDashed, c, rx * f, ry * f);
        }

        foreach (var (_, pos) in Handles(component))
        {
            context.DrawEllipse(Brushes.White, GuideShadow, pos, HandleRadius, HandleRadius);
        }
    }

    private sealed class ImageDrawOperation(
        Rect bounds, SKImage image, SKRect dest, double scale, EditState state,
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
            using var shader = AdjustmentShader.CreateShader(image, state, sampling, m => maskImages.GetValueOrDefault(m.Id));
            using var paint = new SKPaint { Shader = shader };
            canvas.Save();
            canvas.Translate(dest.Left, dest.Top);
            canvas.Scale(dest.Width / image.Width, dest.Height / image.Height);
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
