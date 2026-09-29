using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using PhotoEditor.Core.Viewing;
using SkiaSharp;

namespace PhotoEditor.Controls;

/// <summary>
/// Draws an <see cref="SKBitmap"/> directly on Avalonia's Skia canvas, with zoom
/// (fit / 100% / mouse wheel around the cursor) and drag-to-pan.
/// </summary>
public class ImageViewer : Control
{
    public static readonly StyledProperty<SKBitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageViewer, SKBitmap?>(nameof(Source));

    public static readonly DirectProperty<ImageViewer, double> ZoomProperty =
        AvaloniaProperty.RegisterDirect<ImageViewer, double>(nameof(Zoom), v => v.Zoom);

    private const double WheelZoomStep = 1.25;

    private readonly ViewTransform _view = new();
    private SKImage? _image;
    private Point? _panStart;
    private double _zoom = 1;

    static ImageViewer()
    {
        AffectsRender<ImageViewer>(SourceProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageViewer>(true);
        FocusableProperty.OverrideDefaultValue<ImageViewer>(true);
    }

    public SKBitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

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
            var bitmap = change.GetNewValue<SKBitmap?>();
            _image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
            if (bitmap is not null)
                Update(() => _view.SetImageSize(bitmap.Width, bitmap.Height));
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
        if (_image is null)
            return;
        var p = e.GetPosition(this);
        double factor = Math.Pow(WheelZoomStep, e.Delta.Y);
        Update(() => _view.ZoomBy(factor, p.X, p.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_image is null)
            return;
        var point = e.GetCurrentPoint(this);
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed)
        {
            // Double-click toggles between fit and 100% at the clicked point.
            if (_view.IsFit)
                Update(() => _view.ZoomTo(1, point.Position.X, point.Position.Y));
            else
                Update(_view.ZoomToFit);
            e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _panStart = point.Position;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panStart is not { } start)
            return;
        var p = e.GetPosition(this);
        _panStart = p;
        Update(() => _view.Pan(p.X - start.X, p.Y - start.Y));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndPan(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panStart = null;
        Cursor = null;
    }

    private void EndPan(IPointer pointer)
    {
        if (_panStart is null)
            return;
        _panStart = null;
        pointer.Capture(null);
        Cursor = null;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // makes the whole area hit-testable
        if (_image is null || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var (x0, y0) = _view.ImageToView(0, 0);
        var (x1, y1) = _view.ImageToView(_view.ImageWidth, _view.ImageHeight);
        var dest = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        context.Custom(new ImageDrawOperation(bounds, _image, dest, _view.Scale));
    }

    private sealed class ImageDrawOperation(Rect bounds, SKImage image, SKRect dest, double scale) : ICustomDrawOperation
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
            using var paint = new SKPaint();
            lease.SkCanvas.DrawImage(image, dest, sampling, paint);
        }
    }
}
