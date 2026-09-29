using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace PhotoEditor.Controls;

/// <summary>Draws an <see cref="SKBitmap"/> directly on Avalonia's Skia canvas.</summary>
public class ImageViewer : Control
{
    public static readonly StyledProperty<SKBitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageViewer, SKBitmap?>(nameof(Source));

    private SKImage? _image;

    static ImageViewer()
    {
        AffectsRender<ImageViewer>(SourceProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageViewer>(true);
    }

    public SKBitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            // The render thread may still hold the old image; let the GC release it.
            var bitmap = change.GetNewValue<SKBitmap?>();
            _image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // makes the whole area hit-testable
        if (_image is null || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double scale = Math.Min(1.0, Math.Min(bounds.Width / _image.Width, bounds.Height / _image.Height));
        double w = _image.Width * scale, h = _image.Height * scale;
        var dest = new SKRect(
            (float)((bounds.Width - w) / 2), (float)((bounds.Height - h) / 2),
            (float)((bounds.Width + w) / 2), (float)((bounds.Height + h) / 2));

        context.Custom(new ImageDrawOperation(bounds, _image, dest));
    }

    private sealed class ImageDrawOperation(Rect bounds, SKImage image, SKRect dest) : ICustomDrawOperation
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
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var paint = new SKPaint();
            lease.SkCanvas.DrawImage(image, dest, sampling, paint);
        }
    }
}
