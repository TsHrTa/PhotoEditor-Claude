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
/// Draws an <see cref="SKBitmap"/> directly on Avalonia's Skia canvas, with zoom
/// (fit / 100% / mouse wheel around the cursor) and drag-to-pan.
/// </summary>
public class ImageViewer : Control
{
    public static readonly StyledProperty<PreviewImage?> SourceProperty =
        AvaloniaProperty.Register<ImageViewer, PreviewImage?>(nameof(Source));

    public static readonly StyledProperty<EditState> StateProperty =
        AvaloniaProperty.Register<ImageViewer, EditState>(nameof(State), EditState.Default);

    public static readonly DirectProperty<ImageViewer, double> ZoomProperty =
        AvaloniaProperty.RegisterDirect<ImageViewer, double>(nameof(Zoom), v => v.Zoom);

    private const double WheelZoomStep = 1.25;

    private readonly ViewTransform _view = new();
    private readonly MaskImageCache _maskCache = new();
    private Point? _panStart;
    private double _zoom = 1;

    static ImageViewer()
    {
        AffectsRender<ImageViewer>(SourceProperty, StateProperty);
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
        if (Source is not { } source || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var (x0, y0) = _view.ImageToView(0, 0);
        var (x1, y1) = _view.ImageToView(_view.ImageWidth, _view.ImageHeight);
        var dest = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        // Use the full-resolution image only when the preview would be magnified.
        var image = _view.Scale > source.PreviewScale * 1.01 ? source.Full : source.Preview;
        // Masks are rasterised at preview resolution on the UI thread (cached until they change).
        var state = State;
        var maskImages = state.Masks.Where(m => m.IsActive)
            .ToDictionary(m => m.Id, m => _maskCache.Get(m, source.Preview.Width, source.Preview.Height));
        _maskCache.Retain(state.Masks);
        context.Custom(new ImageDrawOperation(bounds, image, dest, _view.Scale, state, maskImages));
    }

    private sealed class ImageDrawOperation(
        Rect bounds, SKImage image, SKRect dest, double scale, EditState state, Dictionary<Guid, SKImage> maskImages)
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
            canvas.Restore();
        }
    }
}
