using System.Buffers.Binary;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Export;

public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-export-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Little-endian TIFF block with IFD0 = { Make = "Test", Orientation = <paramref name="orientation"/> }.</summary>
    private static byte[] MakeExif(ushort orientation)
    {
        var t = new byte[8 + 2 + 2 * 12 + 4 + 5];
        "II"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), 2);
        // Make: ASCII, 5 chars, stored at offset 38
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(10), 0x010F);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(14), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(18), 38);
        // Orientation: SHORT, 1
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(22), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(24), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(30), orientation);
        // next IFD = 0 at 34..37, then "Test\0"
        "Test\0"u8.CopyTo(t.AsSpan(38));
        return t;
    }

    private static SKBitmap RedBlue()
    {
        var bmp = new SKBitmap(new SKImageInfo(64, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Red);
        using var paint = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(32, 0, 32, 32, paint);
        return bmp;
    }

    /// <summary>A JPEG whose EXIF says "rotate 90° clockwise" (orientation 6).</summary>
    private string WriteRotatedJpeg()
    {
        using var bmp = RedBlue();
        var jpeg = ImageExporter.Encode(bmp, new ExportOptions(ExportFormat.Jpeg, 95));
        var path = Path.Combine(_dir, "source.jpg");
        File.WriteAllBytes(path, ExifMetadata.EmbedInJpeg(jpeg, MakeExif(6)));
        return path;
    }

    [Fact]
    public void EmbeddedExif_IsReadBackAndHonouredBySkia()
    {
        var path = WriteRotatedJpeg();
        var exif = ExifMetadata.Read(path);
        Assert.NotNull(exif);
        Assert.Equal(6, ExifMetadata.GetOrientation(exif));

        using var loaded = ImageLoader.Load(path);
        Assert.Equal((32, 64), (loaded.Width, loaded.Height));
    }

    [Theory]
    [InlineData("out.jpg")]
    [InlineData("out.png")]
    public void Export_KeepsExifWithNormalOrientation(string name)
    {
        var source = WriteRotatedJpeg();
        using var original = ImageLoader.Load(source);
        var dest = Path.Combine(_dir, name);

        ImageExporter.Export(original, new AdjustmentSettings { Exposure = 0.5 }, source, dest,
            new ExportOptions(ExportOptions.FormatFromPath(dest)));

        var exif = ExifMetadata.Read(dest);
        Assert.NotNull(exif);
        Assert.Equal(1, ExifMetadata.GetOrientation(exif));
        Assert.True(exif.AsSpan().IndexOf("Test\0"u8) >= 0, "Make tag lost");

        using var reloaded = ImageLoader.Load(dest); // no second rotation
        Assert.Equal((32, 64), (reloaded.Width, reloaded.Height));
        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact]
    public void Export_AppliesAdjustments()
    {
        using var bmp = TestImages.Solid(new SKColor(100, 100, 100), 8);
        var dest = Path.Combine(_dir, "bright.png");
        ImageExporter.Export(bmp, new AdjustmentSettings { Exposure = 1 }, null, dest, new ExportOptions(ExportFormat.Png));
        using var reloaded = ImageLoader.Load(dest);
        using var expected = CpuAdjustmentRenderer.Render(bmp, new AdjustmentSettings { Exposure = 1 });
        Assert.Equal(expected.GetPixel(3, 3), reloaded.GetPixel(3, 3));
        Assert.Null(ExifMetadata.Read(dest));
    }

    [Fact]
    public void JpegQuality_AffectsFileSize()
    {
        using var bmp = TestImages.Varied(128, 128, withAlpha: false);
        var low = ImageExporter.Encode(bmp, new ExportOptions(ExportFormat.Jpeg, 30));
        var high = ImageExporter.Encode(bmp, new ExportOptions(ExportFormat.Jpeg, 98));
        Assert.True(low.Length < high.Length);
    }

    [Fact]
    public void Crc32_MatchesPngIendChunk() =>
        Assert.Equal(0xAE426082u, ExifMetadata.Crc32("IEND"u8));

    [Fact]
    public void Read_NoExif_ReturnsNull() =>
        Assert.Null(ExifMetadata.Read(ImageExporter.Encode(RedBlue(), new ExportOptions())));
}
