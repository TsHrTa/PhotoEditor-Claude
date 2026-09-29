using System.Buffers.Binary;
using System.Xml.Linq;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Editing;

public class LightroomXmpTests
{
    private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private static readonly ImageGeometry Landscape = new(6000, 4000);

    private static EditState Sample() => new()
    {
        Adjustments = new AdjustmentSettings
        {
            Exposure = 0.75, Contrast = 20, Highlights = -60, Shadows = 45, Whites = 10, Blacks = -15,
            Vibrance = 25, Saturation = -10,
            Blues = new HslBand(-20, 30, -40), Oranges = new HslBand(5, -10, 15),
            VignetteAmount = -30, VignetteMidpoint = 40, VignetteRoundness = 20, VignetteFeather = 70,
            SharpenAmount = 55, SharpenRadius = 1.3, SharpenMasking = 25,
        },
        Crop = new Crop { Left = 0.1, Top = 0.15, Right = 0.8, Bottom = 0.9, Angle = 3.5 },
        Masks =
        [
            new Mask
            {
                Name = "Sky",
                Adjustments = new AdjustmentSettings { Exposure = -1, Highlights = -50, Saturation = 20 },
                Components = [new LinearGradientComponent { Start = new(0.5f, 0.1f), End = new(0.5f, 0.45f) }],
            },
            new Mask
            {
                Name = "Face",
                Enabled = false,
                Adjustments = new AdjustmentSettings { Shadows = 30, Temperature = 10 },
                Components = [new RadialGradientComponent { Center = new(0.4f, 0.6f), RadiusX = 0.1f, RadiusY = 0.2f, Feather = 0.6f, Invert = true }],
            },
        ],
    };

    private static void AssertClose(EditState expected, EditState actual)
    {
        Assert.Equal(expected.Adjustments, actual.Adjustments);
        Assert.Equal(expected.Crop.Left, actual.Crop.Left, 5);
        Assert.Equal(expected.Crop.Top, actual.Crop.Top, 5);
        Assert.Equal(expected.Crop.Right, actual.Crop.Right, 5);
        Assert.Equal(expected.Crop.Bottom, actual.Crop.Bottom, 5);
        Assert.Equal(expected.Crop.Angle, actual.Crop.Angle, 5);
        Assert.Equal(expected.Masks.Count, actual.Masks.Count);
        for (int i = 0; i < expected.Masks.Count; i++)
        {
            var e = expected.Masks[i];
            var a = actual.Masks[i];
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Enabled, a.Enabled);
            Assert.Equal(e.Adjustments, a.Adjustments);
            switch (e.Components[0], a.Components[0])
            {
                case (LinearGradientComponent le, LinearGradientComponent la):
                    Assert.Equal(le.Start.X, la.Start.X, 4);
                    Assert.Equal(le.Start.Y, la.Start.Y, 4);
                    Assert.Equal(le.End.X, la.End.X, 4);
                    Assert.Equal(le.End.Y, la.End.Y, 4);
                    break;
                case (RadialGradientComponent re, RadialGradientComponent ra):
                    Assert.Equal(re.Center.X, ra.Center.X, 4);
                    Assert.Equal(re.Center.Y, ra.Center.Y, 4);
                    Assert.Equal(re.RadiusX, ra.RadiusX, 4);
                    Assert.Equal(re.RadiusY, ra.RadiusY, 4);
                    Assert.Equal(re.Feather, ra.Feather, 4);
                    Assert.Equal(re.Invert, ra.Invert);
                    break;
                default:
                    Assert.Fail($"component type changed: {a.Components[0]}");
                    break;
            }
        }
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft)]
    [InlineData(SKEncodedOrigin.RightTop)]     // portrait, camera turned clockwise
    [InlineData(SKEncodedOrigin.LeftBottom)]   // portrait, camera turned counter-clockwise
    [InlineData(SKEncodedOrigin.BottomRight)]  // upside down
    [InlineData(SKEncodedOrigin.TopRight)]     // mirrored
    public void WriteThenRead_RoundTrips(SKEncodedOrigin orientation)
    {
        var g = new ImageGeometry(4000, 6000, orientation);
        var state = Sample();
        var xml = LightroomXmp.Write(state, g, null, out _);
        AssertClose(state, LightroomXmp.Read(xml, g));
    }

    [Fact]
    public void Write_UsesLightroomNamesAndFormats()
    {
        var xml = LightroomXmp.Write(Sample(), Landscape, null, out _);
        var d = XDocument.Parse(xml).Descendants().First(e => e.Attribute(Crs + "Exposure2012") is not null);
        Assert.Equal("+0.75", d.Attribute(Crs + "Exposure2012")!.Value);
        Assert.Equal("-60", d.Attribute(Crs + "Highlights2012")!.Value);
        Assert.Equal("0", d.Attribute(Crs + "HueAdjustmentRed")!.Value);
        Assert.Equal("-20", d.Attribute(Crs + "HueAdjustmentBlue")!.Value);
        Assert.Equal("-40", d.Attribute(Crs + "LuminanceAdjustmentBlue")!.Value);
        Assert.Equal("True", d.Attribute(Crs + "HasCrop")!.Value);
        Assert.Equal("-3.5", d.Attribute(Crs + "CropAngle")!.Value);
        Assert.Equal("0.100000", d.Attribute(Crs + "CropLeft")!.Value);
        Assert.Equal("As Shot", d.Attribute(Crs + "WhiteBalance")!.Value);
        Assert.StartsWith("<x:xmpmeta", xml);

        var local = XDocument.Parse(xml).Descendants().First(e => (string?)e.Attribute(Crs + "CorrectionName") == "Sky");
        Assert.Equal("-0.250000", local.Attribute(Crs + "LocalExposure2012")!.Value); // -1 EV of ±4
    }

    [Fact]
    public void Write_QuarterTurn_StoresCropInSensorOrientation()
    {
        // Upright portrait 4000×6000 from a sensor image 6000×4000 turned clockwise (orientation 6).
        var g = new ImageGeometry(4000, 6000, SKEncodedOrigin.RightTop);
        var state = new EditState { Crop = new Crop { Left = 0, Top = 0, Right = 0.5, Bottom = 1 } }; // left half
        var d = XDocument.Parse(LightroomXmp.Write(state, g, null, out _)).Descendants()
            .First(e => e.Attribute(Crs + "CropTop") is not null);
        // The upright left half is the sensor's bottom half.
        Assert.Equal(0.5, double.Parse(d.Attribute(Crs + "CropTop")!.Value), 6);
        Assert.Equal(1.0, double.Parse(d.Attribute(Crs + "CropBottom")!.Value), 6);
        Assert.Equal(0.0, double.Parse(d.Attribute(Crs + "CropLeft")!.Value), 6);
        Assert.Equal(1.0, double.Parse(d.Attribute(Crs + "CropRight")!.Value), 6);
    }

    [Fact]
    public void Write_ReportsWhatLightroomCannotRepresent()
    {
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { Temperature = 20 },
            Masks =
            [
                new Mask { Name = "Brush", Adjustments = new AdjustmentSettings { Exposure = 1 }, Components = [new BrushComponent()] },
                new Mask
                {
                    Name = "Grad", Adjustments = new AdjustmentSettings { Exposure = 1, Vibrance = 20 },
                    Components = [new LinearGradientComponent()],
                },
            ],
        };
        var raw = Landscape with { IsRaw = true };
        var xml = LightroomXmp.Write(state, raw, null, out var skipped);
        Assert.Contains(skipped, s => s.Contains("white balance"));
        Assert.Contains(skipped, s => s.Contains("\"Brush\""));
        Assert.Contains(skipped, s => s.Contains("vibrance") && s.Contains("\"Grad\""));
        Assert.Single(LightroomXmp.Read(xml, raw).Masks);
    }

    [Fact]
    public void WhiteBalance_NonRaw_UsesIncrementalValues()
    {
        var state = new EditState { Adjustments = new AdjustmentSettings { Temperature = 35, Tint = -12 } };
        var xml = LightroomXmp.Write(state, Landscape, null, out var skipped);
        Assert.Empty(skipped);
        var d = XDocument.Parse(xml).Descendants().First(e => e.Attribute(Crs + "Exposure2012") is not null);
        Assert.Equal("Custom", d.Attribute(Crs + "WhiteBalance")!.Value);
        Assert.Equal("+35", d.Attribute(Crs + "IncrementalTemperature")!.Value);
        Assert.Equal("-12", d.Attribute(Crs + "IncrementalTint")!.Value);
        Assert.Equal(state.Adjustments, LightroomXmp.Read(xml, Landscape).Adjustments);
        // A RAW file ignores them (its white balance is absolute Kelvin).
        Assert.Equal(0, LightroomXmp.Read(xml, Landscape with { IsRaw = true }).Adjustments.Temperature);
    }

    [Fact]
    public void Write_MergesIntoExistingLightroomSidecar()
    {
        const string existing = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 7.0">
             <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about=""
                xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
                xmlns:dc="http://purl.org/dc/elements/1.1/"
               xmp:Rating="4"
               crs:WhiteBalance="Custom"
               crs:Temperature="5250"
               crs:ColorNoiseReduction="40"
               crs:Exposure2012="+2.00"
               crs:HasSettings="True">
               <dc:subject><rdf:Bag><rdf:li>holiday</rdf:li></rdf:Bag></dc:subject>
              </rdf:Description>
             </rdf:RDF>
            </x:xmpmeta>
            """;
        // A RAW sidecar written by Lightroom (white balance in Kelvin is Lightroom's; keep it).
        var xml = LightroomXmp.Write(new EditState { Adjustments = new AdjustmentSettings { Exposure = -0.5 } },
            Landscape with { IsRaw = true }, existing, out _);
        var d = XDocument.Parse(xml).Descendants().First(e => e.Attribute(Crs + "Exposure2012") is not null);
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        Assert.Equal("4", d.Attribute(xmp + "Rating")!.Value);
        Assert.Equal("Custom", d.Attribute(Crs + "WhiteBalance")!.Value); // Lightroom's white balance kept
        Assert.Equal("5250", d.Attribute(Crs + "Temperature")!.Value);
        Assert.Equal("40", d.Attribute(Crs + "ColorNoiseReduction")!.Value);
        Assert.Equal("-0.50", d.Attribute(Crs + "Exposure2012")!.Value);
        Assert.Contains("holiday", xml);
        Assert.Single(XDocument.Parse(xml).Descendants(XName.Get("Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#")),
            e => e.Parent?.Name.LocalName == "RDF");
    }

    [Fact]
    public void Read_IgnoresUnknownSettingsAndClamps()
    {
        const string xml = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
                 crs:Exposure2012="+9.00" crs:Clarity2012="+30" crs:Shadows2012="+12" crs:HasCrop="False" crs:CropLeft="0.3" />
            </rdf:RDF></x:xmpmeta>
            """;
        var state = LightroomXmp.Read(xml, Landscape);
        Assert.Equal(5, state.Adjustments.Exposure);
        Assert.Equal(12, state.Adjustments.Shadows);
        Assert.True(state.Crop.IsDefault);
    }

    [Theory]
    [InlineData("a.CR3", true)]
    [InlineData("a.nef", true)]
    [InlineData("a.dng", false)]
    [InlineData("a.jpg", false)]
    public void IsProprietaryRaw(string path, bool expected) =>
        Assert.Equal(expected, LightroomXmp.IsProprietaryRaw(path));

    [Fact]
    public void PathFor_ReplacesExtension()
    {
        Assert.Equal(Path.Combine("x", "IMG_0001.xmp"), LightroomXmp.PathFor(Path.Combine("x", "IMG_0001.CR3")));
        Assert.Equal(Path.GetFullPath(Path.Combine("x", "IMG_0001.xmp")),
            Path.GetFullPath(LightroomXmp.PathFor(Path.Combine("x", "IMG_0001.jpg"))));
    }

    [Fact]
    public void PathFor_RawPlusJpegPair_KeepsSeparateSidecars()
    {
        var dir = Directory.CreateTempSubdirectory("pe-xmp-").FullName;
        try
        {
            var raw = Path.Combine(dir, "IMG_0001.CR3");
            var jpg = Path.Combine(dir, "IMG_0001.JPG");
            var dng = Path.Combine(dir, "IMG_0002.dng");
            File.WriteAllText(raw, "");
            File.WriteAllText(jpg, "");
            File.WriteAllText(dng, "");
            Assert.Equal(Path.Combine(dir, "IMG_0001.xmp"), LightroomXmp.PathFor(raw));
            Assert.Equal(jpg + ".xmp", LightroomXmp.PathFor(jpg));
            Assert.Equal(Path.Combine(dir, "IMG_0002.xmp"), LightroomXmp.PathFor(dng)); // alone: normal name
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SaveAndLoad_UseSidecarFile()
    {
        var dir = Directory.CreateTempSubdirectory("pe-xmp-").FullName;
        try
        {
            var image = Path.Combine(dir, "IMG_0001.CR3");
            var state = Sample();
            LightroomXmp.Save(image, state, Landscape);
            Assert.True(File.Exists(Path.Combine(dir, "IMG_0001.xmp")));
            AssertClose(state, LightroomXmp.Load(image, Landscape)!);
            Assert.Null(LightroomXmp.Load(Path.Combine(dir, "other.CR3"), Landscape));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- RAW orientation ----

    private static byte[] Tiff(ushort orientation)
    {
        var t = new byte[8 + 2 + 12 + 4];
        "II"u8.CopyTo(t);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(18), orientation);
        return t;
    }

    [Fact]
    public void RawOrientation_TiffBased() =>
        Assert.Equal(SKEncodedOrigin.RightTop, RawImageLoader.OrientationFromHeader(Tiff(6)));

    [Fact]
    public void RawOrientation_Cr3CmtBox()
    {
        byte[] head = [0, 0, 0, 24, .. "ftypcrx "u8.ToArray(), 0, 0, 0, 1, .. "crx isom"u8.ToArray(),
            0, 0, 0, 34, .. "CMT1"u8.ToArray(), .. Tiff(8)];
        Assert.Equal(SKEncodedOrigin.LeftBottom, RawImageLoader.OrientationFromHeader(head));
    }

    [Fact]
    public void RawOrientation_Unknown_IsTopLeft() =>
        Assert.Equal(SKEncodedOrigin.TopLeft, RawImageLoader.OrientationFromHeader("garbage"u8));
}
