using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public sealed class IccColorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-icc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A 16 × 16 PNG of one colour whose numbers are interpreted in <paramref name="space"/> (null = untagged).</summary>
    private string Png(SKColorSpace? space, byte r, byte g, byte b)
    {
        var info = new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul, space);
        using var bitmap = new SKBitmap(info);
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255;
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void An_sRGB_or_untagged_photo_is_read_as_it_is()
    {
        using var srgb = SKColorSpace.CreateSrgb();
        foreach (var space in new[] { srgb, null })
        {
            using var photo = ImageLoader.Load(Png(space, 200, 100, 50));
            Assert.Equal(new SKColor(200, 100, 50), photo.GetPixel(5, 5));
        }
    }

    [Fact]
    public void An_Adobe_RGB_photo_is_converted_to_sRGB()
    {
        // Adobe RGB mid grey (gamma 2.2) is darker than sRGB's 128 at the same number: 128/255 ^ 2.2 encoded as sRGB.
        using var adobe = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.AdobeRgb);
        using var photo = ImageLoader.Load(Png(adobe, 128, 128, 128));
        var pixel = photo.GetPixel(5, 5);
        double linear = Math.Pow(128 / 255.0, 2.2);
        int expected = (int)Math.Round(ColorMath.LinearToSrgb((float)linear) * 255);
        Assert.InRange(pixel.Red, expected - 1, expected + 1);
        Assert.InRange(Math.Abs(pixel.Red - pixel.Blue), 0, 1);
        Assert.NotEqual(128, pixel.Red);
    }

    [Fact]
    public void A_colour_outside_sRGB_keeps_its_hue_instead_of_clipping_to_a_primary()
    {
        // Display P3 pure green lies outside sRGB. Clipping each channel would give (0, 255, 0); compressing keeps the
        // brightness and gives some of the saturation up, so red and blue are 0 / small and green stays high.
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var photo = ImageLoader.Load(Png(p3, 0, 255, 0));
        var pixel = photo.GetPixel(5, 5);
        Assert.Equal(0, pixel.Red);
        Assert.InRange(pixel.Green, 230, 255);
        Assert.InRange(pixel.Blue, 40, 160);
    }

    [Fact]
    public void Colours_already_inside_the_gamut_are_not_touched()
    {
        float r = 0.2f, g = 0.5f, b = 0.7f;
        IccColor.CompressToGamut(ref r, ref g, ref b);
        Assert.Equal((0.2f, 0.5f, 0.7f), (r, g, b));
        // A colour outside sRGB (a Display P3 green) ends up inside, with the lightness and hue it had (OKLab).
        r = -0.2f; g = 1.04f; b = -0.08f;
        var before = HslMath.Oklch(r, g, b);
        IccColor.CompressToGamut(ref r, ref g, ref b);
        Assert.True(MathF.Min(r, MathF.Min(g, b)) >= 0f);
        var after = HslMath.Oklch(r, g, b);
        Assert.InRange(Math.Abs(after.Lightness - before.Lightness), 0f, 0.01f);
        Assert.InRange(Math.Abs(after.Hue - before.Hue), 0f, 2f);
        Assert.True(after.Chroma < before.Chroma);
    }
}

public sealed class ExportProfileTests
{
    [Theory]
    [InlineData(PhotoEditor.Core.Export.ExportFormat.Jpeg)]
    [InlineData(PhotoEditor.Core.Export.ExportFormat.Png)]
    public void An_export_carries_an_sRGB_profile_and_the_same_pixels(PhotoEditor.Core.Export.ExportFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(180, 90, 40));
        var bytes = PhotoEditor.Core.Export.ImageExporter.Encode(bitmap, new PhotoEditor.Core.Export.ExportOptions(format, 95));
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        Assert.NotNull(codec);
        Assert.NotNull(codec!.Info.ColorSpace);
        Assert.True(codec.Info.ColorSpace!.IsSrgb);
        // Skia's own decode of an sRGB-tagged file does not move the numbers.
        using var decoded = SKBitmap.Decode(bytes);
        var pixel = decoded.GetPixel(10, 10);
        Assert.InRange(pixel.Red, 178, 182);
        Assert.InRange(pixel.Blue, 38, 42);
    }
}
