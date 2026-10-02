using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public sealed class LinearResamplerTests
{
    private static SKBitmap Filled(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                bitmap.SetPixel(x, y, new SKColor(r, g, b));
            }
        return bitmap;
    }

    [Fact]
    public void A_flat_colour_stays_the_same()
    {
        using var source = Filled(64, 48, (_, _) => (200, 120, 30));
        var (photo, layers) = LinearResampler.TryResize(source, null, 20, 15)!.Value;
        using (photo)
        {
            Assert.Null(layers);
            Assert.Equal(new SKColor(200, 120, 30), photo.GetPixel(10, 7));
            Assert.Equal(new SKColor(200, 120, 30), photo.GetPixel(0, 0));
        }
    }

    [Fact]
    public void Black_and_white_stripes_average_in_linear_light()
    {
        // Half white, half black: the light is 50 %, which is sRGB 188, not the 128 that averaging the encoded values gives.
        using var source = Filled(64, 64, (x, _) => x % 2 == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var (photo, _) = LinearResampler.TryResize(source, null, 16, 16)!.Value;
        using (photo)
        {
            int v = photo.GetPixel(8, 8).Red;
            Assert.InRange(v, 185, 191);
        }
    }

    [Fact]
    public void Lanczos_keeps_a_step_edge_sharp()
    {
        using var source = Filled(80, 8, (x, _) => x < 40 ? ((byte)40, (byte)40, (byte)40) : ((byte)200, (byte)200, (byte)200));
        var (photo, _) = LinearResampler.TryResize(source, null, 20, 2)!.Value;
        using (photo)
        {
            // Transition within two pixels, flat on both sides (a box + bilinear would also be flat but wider).
            Assert.InRange(photo.GetPixel(4, 0).Red, 39, 41);
            Assert.InRange(photo.GetPixel(15, 0).Red, 199, 201);
        }
    }

    [Fact]
    public void The_layers_are_filtered_with_the_photo()
    {
        // A bright patch: photo at 255 everywhere, the headroom says it is 2× white (encoded) in the left half.
        int w = 32, h = 32;
        using var source = Filled(w, h, (_, _) => (255, 255, 255));
        var extra = Filled(w, h, (x, _) => x < 16 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var fine = Filled(w, h, (_, _) => (Headroom.FineZero, Headroom.FineZero, Headroom.FineZero));
        var layers = new Headroom(extra, 0.5f, fine);
        var (photo, resized) = LinearResampler.TryResize(source, layers, 8, 8)!.Value;
        using (photo)
        {
            Assert.NotNull(resized);
            Assert.Equal(0.5f, resized!.Scale);
            // Left: 1.5 encoded (still above white), right: exactly white; the photo itself stays 255 in both.
            Assert.Equal(255, photo.GetPixel(1, 4).Red);
            Assert.InRange(resized.Bitmap.GetPixel(1, 4).Red, 250, 255);
            Assert.Equal(0, resized.Bitmap.GetPixel(7, 4).Red);
        }
    }

    [Fact]
    public void The_fraction_of_a_step_survives()
    {
        // A flat dark value that is half a step above 20: the fine layer carries it into the smaller image.
        int w = 16, h = 16;
        using var source = Filled(w, h, (_, _) => (20, 20, 20));
        byte half = (byte)(Headroom.FineZero + 0.5f / 255f / Headroom.FineStep);
        var extra = Filled(w, h, (_, _) => (0, 0, 0));
        var fine = Filled(w, h, (_, _) => (half, half, half));
        var (photo, resized) = LinearResampler.TryResize(source, new Headroom(extra, 0.5f, fine), 4, 4)!.Value;
        using (photo)
        {
            Assert.NotNull(resized?.Fine);
            float exact = photo.GetPixel(2, 2).Red / 255f + Headroom.FineOffset(resized!.Fine!.GetPixel(2, 2).Red);
            Assert.InRange(exact * 255f, 20.45f, 20.55f);
        }
    }

    [Fact]
    public void Transparent_pixels_or_an_enlargement_fall_back_to_the_caller()
    {
        using var source = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.Erase(new SKColor(10, 10, 10, 128));
        Assert.Null(LinearResampler.TryResize(source, null, 4, 4));
        using var opaque = Filled(8, 8, (_, _) => (1, 2, 3));
        Assert.Null(LinearResampler.TryResize(opaque, null, 16, 16));
    }
}

public sealed class PreviewPyramidTests
{
    private static PreviewImage Make()
    {
        var bitmap = new SKBitmap(new SKImageInfo(4000, 2000, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(120, 130, 140));
        return PreviewImage.Create(bitmap);
    }

    [Fact]
    public void TheViewGetsTheSmallestLevelThatIsNotSmallerThanTheScreenSize()
    {
        var preview = Make(); // 4000 px wide, preview 2560
        Assert.Same(preview.Full, preview.ImageFor(1.0));                  // zoomed past the preview: full
        Assert.Same(preview.Preview, preview.ImageFor(0.64));              // about the preview's own scale
        foreach (double scale in new[] { 0.5, 0.3, 0.2, 0.12 })
        {
            var image = preview.ImageFor(scale);
            double levelScale = (double)image.Width / preview.Width;
            Assert.True(levelScale >= scale - 1e-9, $"scale {scale}: level {levelScale:0.000} would be magnified");
            Assert.True(levelScale < scale * 1.5 || image.Width <= 400, $"scale {scale}: level {levelScale:0.000} is far above the screen size");
        }
        Assert.Same(preview.ImageFor(0.3), preview.ImageFor(0.3)); // made once
        Assert.True(preview.ImageFor(0.2).Width < preview.ImageFor(0.5).Width);
    }

    [Fact]
    public void ALevelOfARawKeepsItsLayers()
    {
        var bitmap = new SKBitmap(new SKImageInfo(3000, 2000, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(255, 255, 255));
        var extra = new SKBitmap(new SKImageInfo(3000, 2000, SKColorType.Rgba8888, SKAlphaType.Opaque));
        extra.Erase(new SKColor(100, 100, 100));
        Headroom.Attach(bitmap, new Headroom(extra, 0.5f) { BaseCurve = true });
        var preview = PreviewImage.Create(bitmap);
        var level = preview.ImageFor(0.2);
        Assert.NotSame(preview.Preview, level);
        var layers = Headroom.Of(level);
        Assert.NotNull(layers);
        Assert.True(layers!.BaseCurve);
        Assert.Equal(level.Width, layers.Width);
    }
}
