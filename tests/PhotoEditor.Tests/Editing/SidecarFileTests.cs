using System.Text.Json;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;

namespace PhotoEditor.Tests.Editing;

public sealed class SidecarFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-sidecar-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly AdjustmentSettings Sample = new()
    {
        Exposure = 0.75,
        Contrast = 20,
        Shadows = -15,
        Temperature = 12,
        Vibrance = 30,
        Blues = new HslBand(-10, 25, -40),
        Reds = new HslBand(5, 0, 0),
    };

    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var doc = new EditDocument { Adjustments = Sample };
        var back = SidecarFile.Deserialize(SidecarFile.Serialize(doc));
        Assert.Equal(doc, back);
    }

    [Fact]
    public void Json_UsesReadableCamelCaseNames()
    {
        var json = SidecarFile.Serialize(new EditDocument { Adjustments = Sample });
        Assert.Contains("\"exposure\": 0.75", json);
        Assert.Contains("\"blues\"", json);
        Assert.DoesNotContain("isDefault", json);
    }

    [Fact]
    public void MissingAndUnknownFields_AreTolerated()
    {
        var doc = SidecarFile.Deserialize("""{ "adjustments": { "exposure": 1.5, "futureThing": 3, "greens": { "hue": 10 } }, "masks": [] }""");
        Assert.Equal(1.5, doc.Adjustments.Exposure);
        Assert.Equal(0, doc.Adjustments.Contrast);
        Assert.Equal(new HslBand(10, 0, 0), doc.Adjustments.Greens);
        Assert.Equal(HslBand.Zero, doc.Adjustments.Reds);
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var doc = SidecarFile.Deserialize("""{ "adjustments": { "exposure": 99, "reds": { "saturation": -500 } } }""");
        Assert.Equal(5, doc.Adjustments.Exposure);
        Assert.Equal(-100, doc.Adjustments.Reds.Saturation);
    }

    [Fact]
    public void NullBand_BecomesZero()
    {
        var doc = SidecarFile.Deserialize("""{ "adjustments": { "reds": null } }""");
        Assert.Equal(HslBand.Zero, doc.Adjustments.Reds);
    }

    [Fact]
    public void InvalidJson_Throws() =>
        Assert.ThrowsAny<JsonException>(() => SidecarFile.Deserialize("{ not json"));

    [Fact]
    public void SaveLoad_UsesFileNextToImage()
    {
        var image = Path.Combine(_dir, "photo.jpg");
        Assert.Null(SidecarFile.Load(image));

        SidecarFile.Save(image, new EditDocument { Adjustments = Sample });
        Assert.True(File.Exists(Path.Combine(_dir, "photo.jpg.json")));
        Assert.Equal(Sample, SidecarFile.Load(image)!.Adjustments);
        Assert.False(File.Exists(Path.Combine(_dir, "photo.jpg.json.tmp")));
    }
}

public class SidecarMaskTests
{
    [Fact]
    public void MasksWithoutKnownComponents_RoundTrip()
    {
        var doc = new EditDocument
        {
            Masks = [new PhotoEditor.Core.Masks.Mask { Name = "Sky", Adjustments = new AdjustmentSettings { Exposure = -0.5 } }],
        };
        var back = SidecarFile.Deserialize(SidecarFile.Serialize(doc));
        Assert.Equal(doc, back);
        Assert.Equal("Sky", back.Masks[0].Name);
    }
}
