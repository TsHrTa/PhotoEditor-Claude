using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public class ImageLoaderTests
{
    // 2x1 image: red at (0,0), blue at (1,0).
    private static SKBitmap TwoPixels()
    {
        var bmp = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.SetPixel(0, 0, SKColors.Red);
        bmp.SetPixel(1, 0, SKColors.Blue);
        return bmp;
    }

    [Fact]
    public void Load_PngRoundTrip_KeepsSizeAndPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pe-{Guid.NewGuid():N}.png");
        try
        {
            using (var src = TwoPixels())
            using (var data = src.Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(path, data.ToArray());

            using var loaded = ImageLoader.Load(path);
            Assert.Equal(2, loaded.Width);
            Assert.Equal(1, loaded.Height);
            Assert.Equal(SKColors.Red, loaded.GetPixel(0, 0));
            Assert.Equal(SKColors.Blue, loaded.GetPixel(1, 0));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_GarbageFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pe-{Guid.NewGuid():N}.jpg");
        File.WriteAllText(path, "not an image");
        try { Assert.Throws<InvalidDataException>(() => ImageLoader.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyOrientation_TopRight_MirrorsHorizontally()
    {
        using var src = TwoPixels();
        using var r = ImageLoader.ApplyOrientation(src, SKEncodedOrigin.TopRight);
        Assert.Equal(SKColors.Blue, r.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, r.GetPixel(1, 0));
    }

    [Fact]
    public void ApplyOrientation_RightTop_RotatesClockwise()
    {
        // Rotating 90° clockwise turns a 2x1 row into a 1x2 column with the left pixel on top.
        using var src = TwoPixels();
        using var r = ImageLoader.ApplyOrientation(src, SKEncodedOrigin.RightTop);
        Assert.Equal(1, r.Width);
        Assert.Equal(2, r.Height);
        Assert.Equal(SKColors.Red, r.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, r.GetPixel(0, 1));
    }

    [Fact]
    public void ApplyOrientation_LeftBottom_RotatesCounterClockwise()
    {
        using var src = TwoPixels();
        using var r = ImageLoader.ApplyOrientation(src, SKEncodedOrigin.LeftBottom);
        Assert.Equal(1, r.Width);
        Assert.Equal(2, r.Height);
        Assert.Equal(SKColors.Blue, r.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, r.GetPixel(0, 1));
    }

    [Theory]
    [InlineData("a.JPG", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.txt", false)]
    public void IsSupported_ChecksExtension(string path, bool expected) =>
        Assert.Equal(expected, ImageLoader.IsSupported(path));
}
