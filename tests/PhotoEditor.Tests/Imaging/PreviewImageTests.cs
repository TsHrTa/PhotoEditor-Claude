using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public class PreviewImageTests
{
    [Theory]
    [InlineData(6000, 4000, 2560, 2560, 1707)]
    [InlineData(4000, 6000, 2560, 1707, 2560)]
    [InlineData(800, 600, 2560, 800, 600)]
    public void PreviewSize_FitsLongSide(int w, int h, int max, int ew, int eh) =>
        Assert.Equal((ew, eh), PreviewImage.PreviewSize(w, h, max));

    [Fact]
    public void Create_DownscalesLargeImage()
    {
        using var bmp = new SKBitmap(new SKImageInfo(400, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Orange);
        var p = PreviewImage.Create(bmp, 100);
        Assert.Equal(400, p.Width);
        Assert.Equal(100, p.Preview.Width);
        Assert.Equal(50, p.Preview.Height);
        Assert.Equal(0.25, p.PreviewScale, 9);
    }

    [Fact]
    public void Create_SmallImage_UsesFullAsPreview()
    {
        using var bmp = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        var p = PreviewImage.Create(bmp, 100);
        Assert.Same(p.Full, p.Preview);
    }
}
