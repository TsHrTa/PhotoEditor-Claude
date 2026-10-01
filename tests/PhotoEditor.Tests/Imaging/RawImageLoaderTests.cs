using ImageMagick;
using PhotoEditor.Core.Imaging;

namespace PhotoEditor.Tests.Imaging;

public class RawImageLoaderTests
{
    [Theory]
    [InlineData("IMG_0001.CR3", true)]
    [InlineData("a.cr2", true)]
    [InlineData("a.NEF", true)]
    [InlineData("a.dng", true)]
    [InlineData("a.jpg", false)]
    public void IsRaw_ChecksExtension(string path, bool expected)
    {
        Assert.Equal(expected, RawImageLoader.IsRaw(path));
        Assert.True(ImageLoader.IsSupported(path));
    }

    [Theory]
    [InlineData("1/2e+01", 0.05)]
    [InlineData("5e+01 mm", 50.0)]
    [InlineData("3.2e+03", 3200.0)]
    [InlineData("0.00", 0.0)]
    [InlineData("1/0", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParseNumber_HandlesLibRawFormats(string text, double? expected)
    {
        var value = RawImageLoader.ParseNumber(text);
        if (expected is null)
            Assert.Null(value);
        else
            Assert.Equal(expected.Value, value!.Value, 6);
    }

    private static readonly Dictionary<string, string> Canon5D = new()
    {
        ["dng:make"] = "Canon",
        ["dng:camera.model.name"] = "EOS 5D Mark II",
        ["dng:create.date"] = "2008-10-29T20:05:00+00:00",
        ["dng:exposure.time"] = "1/2e+01",
        ["dng:f.number"] = "1",
        ["dng:focal.length"] = "5e+01 mm",
        ["dng:iso.setting"] = "3.2e+03",
    };

    [Fact]
    public void ParseMetadata_ReadsCameraValues()
    {
        var m = RawImageLoader.ParseMetadata(Canon5D.GetValueOrDefault);
        Assert.Equal("Canon", m.Make);
        Assert.Equal("EOS 5D Mark II", m.Model);
        Assert.Equal(new DateTime(2008, 10, 29, 20, 5, 0), m.DateTaken);
        Assert.Equal(0.05, m.ExposureTime!.Value, 6);
        Assert.Null(m.FNumber); // 1 = not reported by the lens
        Assert.Equal(50, m.FocalLength);
        Assert.Equal(3200, m.Iso);
    }

    [Fact]
    public void BuildExif_ProducesReadableTiffBlock()
    {
        var m = RawImageLoader.ParseMetadata(Canon5D.GetValueOrDefault) with { FNumber = 2.8 };
        var tiff = RawImageLoader.BuildExif(m);
        Assert.NotNull(tiff);
        Assert.True(tiff![0] == (byte)'I' || tiff[0] == (byte)'M'); // starts with the TIFF header

        var profile = new ExifProfile([.. "Exif\0\0"u8.ToArray(), .. tiff]);
        Assert.Equal("Canon", profile.GetValue(ExifTag.Make)?.Value);
        Assert.Equal("EOS 5D Mark II", profile.GetValue(ExifTag.Model)?.Value);
        Assert.Equal("2008:10:29 20:05:00", profile.GetValue(ExifTag.DateTimeOriginal)?.Value);
        Assert.Equal(new Rational(1, 20), profile.GetValue(ExifTag.ExposureTime)?.Value);
        Assert.Equal(2.8, profile.GetValue(ExifTag.FNumber)!.Value.ToDouble(), 6);
        Assert.Equal((ushort)3200, profile.GetValue(ExifTag.ISOSpeedRatings)!.Value![0]);
    }

    [Fact]
    public void BuildExif_NothingKnown_ReturnsNull() =>
        Assert.Null(RawImageLoader.BuildExif(new RawMetadata()));

    [Fact]
    public void Load_GarbageRawFile_ThrowsInvalidData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pe-{Guid.NewGuid():N}.cr3");
        File.WriteAllText(path, "not a raw file");
        try { Assert.Throws<InvalidDataException>(() => ImageLoader.Load(path)); }
        finally { File.Delete(path); }
    }
}
