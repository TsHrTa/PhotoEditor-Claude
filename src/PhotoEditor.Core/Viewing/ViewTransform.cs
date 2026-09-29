namespace PhotoEditor.Core.Viewing;

/// <summary>
/// Maps image pixels to viewport coordinates: <c>view = image * Scale + Offset</c>.
/// Handles fit-to-view, zooming around a point and panning with clamping.
/// </summary>
public sealed class ViewTransform
{
    public const double MinScale = 0.02;
    public const double MaxScale = 32;

    public double ImageWidth { get; private set; }
    public double ImageHeight { get; private set; }
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }

    /// <summary>True while the image is kept fitted to the view (also across view resizes).</summary>
    public bool IsFit { get; private set; } = true;
    public double Scale { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    /// <summary>Scale at which the whole image fits into the view.</summary>
    public double FitScale =>
        ImageWidth <= 0 || ImageHeight <= 0 || ViewWidth <= 0 || ViewHeight <= 0
            ? 1
            : Math.Min(ViewWidth / ImageWidth, ViewHeight / ImageHeight);

    public void SetImageSize(double width, double height)
    {
        ImageWidth = width;
        ImageHeight = height;
        ZoomToFit();
    }

    public void SetViewSize(double width, double height)
    {
        // Keep the image point at the view centre in place when the view is resized.
        var (cx, cy) = ViewToImage(ViewWidth / 2, ViewHeight / 2);
        ViewWidth = width;
        ViewHeight = height;
        if (IsFit)
        {
            ZoomToFit();
            return;
        }
        OffsetX = width / 2 - cx * Scale;
        OffsetY = height / 2 - cy * Scale;
        Clamp();
    }

    public void ZoomToFit()
    {
        IsFit = true;
        Scale = FitScale;
        Clamp();
    }

    /// <summary>Sets an absolute scale, keeping the image point under (<paramref name="viewX"/>, <paramref name="viewY"/>) fixed.</summary>
    public void ZoomTo(double scale, double viewX, double viewY)
    {
        var (ix, iy) = ViewToImage(viewX, viewY);
        IsFit = false;
        Scale = Math.Clamp(scale, Math.Min(MinScale, FitScale), MaxScale);
        OffsetX = viewX - ix * Scale;
        OffsetY = viewY - iy * Scale;
        Clamp();
    }

    /// <summary>Zooms to 100% around the view centre.</summary>
    public void ZoomToActualSize() => ZoomTo(1, ViewWidth / 2, ViewHeight / 2);

    /// <summary>Multiplies the scale by <paramref name="factor"/> around a view point (e.g. the mouse).</summary>
    public void ZoomBy(double factor, double viewX, double viewY) => ZoomTo(Scale * factor, viewX, viewY);

    public void Pan(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
        Clamp();
    }

    public (double X, double Y) ImageToView(double x, double y) => (x * Scale + OffsetX, y * Scale + OffsetY);

    public (double X, double Y) ViewToImage(double x, double y) => ((x - OffsetX) / Scale, (y - OffsetY) / Scale);

    /// <summary>Centres the image on an axis where it is smaller than the view; otherwise keeps the view covered.</summary>
    private void Clamp()
    {
        OffsetX = ClampAxis(OffsetX, ImageWidth * Scale, ViewWidth);
        OffsetY = ClampAxis(OffsetY, ImageHeight * Scale, ViewHeight);
    }

    private static double ClampAxis(double offset, double content, double view) =>
        content <= view ? (view - content) / 2 : Math.Clamp(offset, view - content, 0);
}
