using PhotoEditor.Core.Editing;
using SkiaSharp;

namespace PhotoEditor.Tests.Editing;

public sealed class PhotoOrientationTests
{
    /// <summary>3 × 2 image with a distinct colour per pixel.</summary>
    private static SKBitmap Grid()
    {
        var bmp = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x * 80 + 10), (byte)(y * 120 + 10), 50));
        return bmp;
    }

    private static SKColor[] Pixels(SKBitmap b) =>
        Enumerable.Range(0, b.Width * b.Height).Select(i => b.GetPixel(i % b.Width, i / b.Width)).ToArray();

    private static SKBitmap Rotate90(SKBitmap b)
    {
        var r = new SKBitmap(new SKImageInfo(b.Height, b.Width, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                r.SetPixel(b.Height - 1 - y, x, b.GetPixel(x, y)); // clockwise
        return r;
    }

    private static SKBitmap Mirror(SKBitmap b)
    {
        var r = new SKBitmap(b.Info);
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                r.SetPixel(b.Width - 1 - x, y, b.GetPixel(x, y));
        return r;
    }

    [Fact]
    public void Apply_RotatesClockwiseThenMirrors()
    {
        using var grid = Grid();
        using var expected = Rotate90(grid);
        using var turned = new PhotoOrientation(1).Apply(grid);
        Assert.Equal(Pixels(expected), Pixels(turned));
        using var mirrored = Mirror(expected);
        using var both = new PhotoOrientation(1, true).Apply(grid);
        Assert.Equal(Pixels(mirrored), Pixels(both));
    }

    [Fact]
    public void Operations_ActOnThePictureAsShown()
    {
        using var grid = Grid();
        foreach (var start in PhotoOrientation.All)
        {
            var shown = start.Apply(grid.Copy());
            Assert.Equal(Pixels(Rotate90(shown)), Pixels(start.RotatedClockwise().Apply(grid.Copy())));
            Assert.Equal(Pixels(Mirror(shown)), Pixels(start.FlippedHorizontally().Apply(grid.Copy())));
            Assert.Equal(Pixels(Rotate90(Rotate90(Rotate90(shown)))), Pixels(start.RotatedCounterClockwise().Apply(grid.Copy())));
            Assert.Equal(Pixels(Rotate90(Rotate90(Mirror(shown)))), Pixels(start.FlippedVertically().Apply(grid.Copy())));
        }
    }

    [Fact]
    public void ExifCodes_RoundTrip_AndCompose()
    {
        foreach (var o in PhotoOrientation.All)
        {
            Assert.Equal(o, PhotoOrientation.FromOrigin(o.ToOrigin()));
            Assert.True(o.Inverse().After(o).IsNone);
        }
        using var grid = Grid();
        foreach (var a in PhotoOrientation.All)
            foreach (var b in PhotoOrientation.All)
            {
                using var first = a.Apply(grid.Copy());
                using var twice = b.Apply(first.Copy());
                using var composed = b.After(a).Apply(grid.Copy());
                Assert.Equal(Pixels(twice), Pixels(composed));
            }
    }
}
