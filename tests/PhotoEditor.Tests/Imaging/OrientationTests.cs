using System.Buffers.Binary;
using ImageMagick;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

/// <summary>
/// Photos taken with the camera turned (EXIF orientation 1–8) open upright, in the editor and in the filmstrip.
/// The test files are made with ImageMagick, independently of the app's own rotation code.
/// </summary>
public sealed class OrientationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orient-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A 60 × 40 JPEG that shows red / green / blue / white quadrants (top-left, top-right, bottom-left,
    /// bottom-right) once turned by its EXIF <paramref name="orientation"/>: the pixels are stored turned back.
    /// </summary>
    private string Jpeg(int orientation)
    {
        using var image = new MagickImage(MagickColors.Black, 60, 40);
        image.Draw(new ImageMagick.Drawing.Drawables()
            .FillColor(MagickColors.Red).Rectangle(0, 0, 29, 19)
            .FillColor(MagickColors.Lime).Rectangle(30, 0, 59, 19)
            .FillColor(MagickColors.Blue).Rectangle(0, 20, 29, 39)
            .FillColor(MagickColors.White).Rectangle(30, 20, 59, 39));
        switch (orientation)
        {
            case 2: image.Flop(); break;
            case 3: image.Rotate(180); break;
            case 4: image.Flip(); break;
            case 5: image.Transpose(); break;
            case 6: image.Rotate(-90); break;
            case 7: image.Transverse(); break;
            case 8: image.Rotate(90); break;
        }
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        image.SetProfile(exif);
        image.Orientation = (OrientationType)orientation; // (written into the EXIF on save)
        image.Quality = 95;
        var path = Path.Combine(_dir, $"o{orientation}.jpg");
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    /// <summary>The colours of the four quadrants (R, G, B, W; ? = none of them).</summary>
    private static string Quadrants(SKBitmap b)
    {
        string At(int x, int y)
        {
            var c = b.GetPixel(x, y);
            return c.Red > 128 && c.Green > 128 && c.Blue > 128 ? "W" : c.Red > 128 ? "R" : c.Green > 128 ? "G" : c.Blue > 128 ? "B" : "?";
        }
        return At(b.Width / 4, b.Height / 4) + At(3 * b.Width / 4, b.Height / 4) + At(b.Width / 4, 3 * b.Height / 4) + At(3 * b.Width / 4, 3 * b.Height / 4);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Jpeg_OpensUpright_InTheEditorAndTheFilmstrip(int orientation)
    {
        var path = Jpeg(orientation);
        Assert.Equal((SKEncodedOrigin)orientation, ImageLoader.ReadOrientation(path));
        using var full = ImageLoader.Load(path);
        Assert.Equal((60, 40), (full.Width, full.Height));
        Assert.Equal("RGBW", Quadrants(full));
        using var thumbnail = ThumbnailCache.Create(path, 30)!;
        Assert.Equal((30, 20), (thumbnail.Width, thumbnail.Height));
        Assert.Equal("RGBW", Quadrants(thumbnail));
    }

    /// <summary>
    /// Real Canon CR2 when PHOTOEDITOR_RAW points to one: a copy with the orientation tag set to 3 / 6 / 8 (as if the
    /// camera had been turned) decodes turned the same way as its embedded preview and filmstrip thumbnail.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void Raw_FullDecodeIsTurnedLikeItsPreview(int orientation)
    {
        var source = Environment.GetEnvironmentVariable("PHOTOEDITOR_RAW");
        if (source is null || !File.Exists(source) || !source.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase))
            return;
        var bytes = File.ReadAllBytes(source);
        Assert.True(bytes[0] == 'I' && bytes[1] == 'I', "expects a little-endian CR2");
        int ifd0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifd0));
        int entry = Enumerable.Range(0, entries).Select(i => ifd0 + 2 + 12 * i)
            .First(e => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(e)) == 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entry + 8), (ushort)orientation);
        var path = Path.Combine(_dir, $"turned{orientation}.CR2");
        File.WriteAllBytes(path, bytes);

        using var upright = EmbeddedPreview.Load(source, 800)!;
        using var expected = ImageLoader.ApplyOrientation(upright, (SKEncodedOrigin)orientation);
        using var full = RawImageLoader.Load(path);
        using var preview = EmbeddedPreview.Load(path, 800)!;
        using var thumbnail = ThumbnailCache.Create(path, 200)!;
        bool portrait = orientation is 6 or 8;
        foreach (var b in new[] { full, preview, thumbnail })
        {
            Assert.Equal(portrait, b.Height > b.Width);
            Assert.True(Similarity(b, expected) > 0.9, $"{b.Width} × {b.Height}: {Similarity(b, expected):0.00}");
        }
    }

    /// <summary>Correlation of the brightness of two pictures (scaled to the same small size).</summary>
    private static double Similarity(SKBitmap a, SKBitmap b)
    {
        float[] Small(SKBitmap bitmap)
        {
            using var s = bitmap.Resize(new SKImageInfo(48, 48 * b.Height / b.Width), SKSamplingOptions.Default)!;
            return Enumerable.Range(0, s.Width * s.Height)
                .Select(i => { var c = s.GetPixel(i % s.Width, i / s.Width); return (c.Red + c.Green + c.Blue) / 3f; }).ToArray();
        }
        var x = Small(a);
        var y = Small(b);
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxy / Math.Sqrt(sxx * syy);
    }
}
