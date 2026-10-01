using ImageMagick;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public sealed class TiffTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-tiff-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A 64 × 8 TIFF whose red goes 1000 ... 1000 + 63 in 16-bit steps (a sixteen-bit slope far below one 8-bit step).</summary>
    private string DeepTiff()
    {
        var samples = new ushort[64 * 8 * 3];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 64; x++)
                for (int c = 0; c < 3; c++)
                    samples[(y * 64 + x) * 3 + c] = (ushort)(1000 + x * 4);
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var image = new MagickImage();
        image.ReadPixels(bytes, new PixelReadSettings(64, 8, StorageType.Short, PixelMapping.RGB));
        image.Depth = 16;
        var path = Path.Combine(_dir, "deep.tif");
        image.Write(path, MagickFormat.Tiff);
        return path;
    }

    [Fact]
    public void A_16_bit_tiff_keeps_its_precision_in_the_fine_layer()
    {
        var path = DeepTiff();
        Assert.True(ImageLoader.IsSupported(path));
        using var photo = ImageLoader.Load(path);
        Assert.Equal(64, photo.Width);
        var layers = Headroom.Of(photo);
        Assert.NotNull(layers?.Fine);
        for (int x = 0; x < 64; x += 7)
        {
            double exact = (1000 + x * 4) / 65535.0;
            double got = photo.GetPixel(x, 3).Red / 255.0 + Headroom.FineOffset(layers!.Fine!.GetPixel(x, 3).Red);
            Assert.InRange(Math.Abs(got - exact), 0, 1.0 / 65535 * 1.6);
        }
    }

    [Fact]
    public void The_geometry_is_read_without_decoding()
    {
        var geometry = EditStore.ReadGeometry(DeepTiff());
        Assert.Equal((64, 8), (geometry.Width, geometry.Height));
    }

    [Fact]
    public void A_float_render_exports_to_a_16_bit_tiff_without_rounding_to_8_bits()
    {
        // A slope of 1/4 of an 8-bit step per pixel: an 8-bit export has four equal pixels per level, a 16-bit one keeps the slope.
        int w = 256;
        using var source = new SKBitmap(new SKImageInfo(w, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        source.Erase(new SKColor(128, 128, 128));
        using var f16 = new SKBitmap(new SKImageInfo(w, 4, SKColorType.RgbaF16, SKAlphaType.Premul));
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(f16.GetPixelSpan());
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                halves[i] = halves[i + 1] = halves[i + 2] = (Half)((100f + x / 4f) / 255f);
                halves[i + 3] = (Half)1f;
            }
        var bytes = TiffImageWriter.Encode(f16);
        using var back = new MagickImage(bytes);
        Assert.Equal(16u, back.Depth);
        var samples = back.GetPixelsUnsafe().ToShortArray("RGB")!;
        // Values rise by about 257/4 = 64 per pixel, not in 8-bit steps of 257.
        int a = samples[100 * 3], b = samples[101 * 3];
        Assert.InRange(b - a, 55, 75);
        Assert.Contains("sRGB", back.GetColorProfile()?.Description ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exporting_a_photo_as_tif_writes_a_readable_16_bit_file_and_is_readable()
    {
        using var source = TestImages.Varied(48, 32, withAlpha: false);
        var path = Path.Combine(_dir, "out.tif");
        ImageExporter.Export(source, new EditState(), null, path, new ExportOptions(ExportOptions.FormatFromPath(path)));
        using var back = new MagickImage(path);
        Assert.Equal((48u, 32u), (back.Width, back.Height));
        Assert.Equal(16u, back.Depth);
    }
}
