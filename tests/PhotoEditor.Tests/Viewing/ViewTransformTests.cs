using PhotoEditor.Core.Viewing;

namespace PhotoEditor.Tests.Viewing;

public class ViewTransformTests
{
    private static ViewTransform Create(double iw = 2000, double ih = 1000, double vw = 1000, double vh = 1000)
    {
        var t = new ViewTransform();
        t.SetViewSize(vw, vh);
        t.SetImageSize(iw, ih);
        return t;
    }

    [Fact]
    public void Fit_ScalesToSmallerRatioAndCentres()
    {
        var t = Create();
        Assert.True(t.IsFit);
        Assert.Equal(0.5, t.Scale, 9);
        Assert.Equal(0, t.OffsetX, 9);
        Assert.Equal(250, t.OffsetY, 9); // 500px tall image centred in 1000px view
    }

    [Fact]
    public void Fit_FollowsViewResize()
    {
        var t = Create();
        t.SetViewSize(500, 500);
        Assert.Equal(0.25, t.Scale, 9);
    }

    [Fact]
    public void ZoomTo_KeepsAnchorPointFixed()
    {
        var t = Create();
        var before = t.ViewToImage(600, 500);
        t.ZoomTo(1, 600, 500);
        var after = t.ViewToImage(600, 500);
        Assert.False(t.IsFit);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void Pan_IsClampedToImageEdges()
    {
        var t = Create();
        t.ZoomToActualSize();
        t.Pan(10_000, 10_000);
        Assert.Equal(0, t.OffsetX, 9);   // left edge of image at left edge of view
        Assert.Equal(0, t.OffsetY, 9);
        t.Pan(-10_000, -10_000);
        Assert.Equal(1000 - 2000, t.OffsetX, 9);
        Assert.Equal(1000 - 1000, t.OffsetY, 9);
    }

    [Fact]
    public void SmallerThanView_StaysCentred()
    {
        var t = Create(200, 100);
        t.ZoomToActualSize();
        t.Pan(123, 45);
        Assert.Equal(400, t.OffsetX, 9);
        Assert.Equal(450, t.OffsetY, 9);
    }

    [Fact]
    public void ZoomBy_ClampsToMaxScale()
    {
        var t = Create();
        for (int i = 0; i < 100; i++) t.ZoomBy(2, 500, 500);
        Assert.Equal(ViewTransform.MaxScale, t.Scale);
    }

    [Fact]
    public void ImageToView_RoundTrips()
    {
        var t = Create();
        t.ZoomTo(1.7, 321, 654);
        var v = t.ImageToView(123, 456);
        var i = t.ViewToImage(v.X, v.Y);
        Assert.Equal(123, i.X, 9);
        Assert.Equal(456, i.Y, 9);
    }
}
