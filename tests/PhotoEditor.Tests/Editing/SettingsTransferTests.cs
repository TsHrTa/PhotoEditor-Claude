using System.Buffers.Binary;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Editing;

public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pe-paste-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly EditState Source = new()
    {
        Adjustments = new AdjustmentSettings
        {
            Exposure = 1.2, Shadows = 30, Temperature = 15, Vibrance = 20, Blues = new HslBand(-10, 5, 0),
            VignetteAmount = -25, SharpenAmount = 50, SharpenRadius = 1.4,
        },
        Crop = new Crop { Left = 0.1, Top = 0.1, Right = 0.9, Bottom = 0.9, Angle = 8 },
        Masks = [new Mask { Name = "Sky", Adjustments = new AdjustmentSettings { Exposure = -1 }, Components = [new LinearGradientComponent()] }],
    };

    private static readonly EditState Target = new()
    {
        Adjustments = new AdjustmentSettings { Contrast = 40, Saturation = -20, Reds = new HslBand(5, 5, 5), VignetteAmount = 10 },
        Crop = new Crop { Left = 0.2, Right = 0.6 },
    };

    [Fact]
    public void Apply_OnlyLight_KeepsEverythingElse()
    {
        var r = SettingsTransfer.Apply(Target, Source, SettingsGroups.Light, 600, 400);
        Assert.Equal(1.2, r.Adjustments.Exposure);
        Assert.Equal(30, r.Adjustments.Shadows);
        Assert.Equal(0, r.Adjustments.Contrast); // part of Light: copied (source has 0)
        Assert.Equal(-20, r.Adjustments.Saturation);
        Assert.Equal(new HslBand(5, 5, 5), r.Adjustments.Reds);
        Assert.Equal(10, r.Adjustments.VignetteAmount);
        Assert.Equal(Target.Crop, r.Crop);
        Assert.Empty(r.Masks);
    }

    [Fact]
    public void Apply_Default_CopiesAdjustmentsButNotCropOrMasks()
    {
        var r = SettingsTransfer.Apply(Target, Source, SettingsGroups.Default, 600, 400);
        Assert.Equal(Source.Adjustments, r.Adjustments);
        Assert.Equal(Target.Crop, r.Crop);
        Assert.Empty(r.Masks);
    }

    [Fact]
    public void Apply_All_CopiesCropAndMasksWithNewIds()
    {
        var r = SettingsTransfer.Apply(Target, Source, SettingsGroups.All, 600, 400);
        Assert.Equal(Source.Adjustments, r.Adjustments);
        Assert.Single(r.Masks);
        Assert.NotEqual(Source.Masks[0].Id, r.Masks[0].Id);
        Assert.Equal(Source.Masks[0].Components, r.Masks[0].Components);
        Assert.True(r.Crop.Frame(600, 400).IsInside(600, 400));
    }

    [Fact]
    public void Apply_RotatedCrop_IsRefittedToAPortraitTarget()
    {
        var source = new EditState { Crop = new Crop { Angle = 10 } }; // whole frame turned: sticks out of any photo
        Assert.False(source.Crop.Frame(400, 600).IsInside(400, 600));
        var r = SettingsTransfer.Apply(EditState.Default, source, SettingsGroups.Crop, 400, 600);
        Assert.Equal(10, r.Crop.Angle);
        Assert.True(r.Crop.Frame(400, 600).IsInside(400, 600));
    }

    private string WriteJpeg(string name, int width, int height, ushort? orientation = null)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Gray);
        var bytes = ImageExporter.Encode(bmp, new ExportOptions());
        if (orientation is { } o)
            bytes = ExifMetadata.EmbedInJpeg(bytes, OrientationExif(o));
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] OrientationExif(ushort orientation)
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
    public void ReadGeometry_UprightSizeAndOrientation()
    {
        var plain = EditStore.ReadGeometry(WriteJpeg("a.jpg", 64, 32));
        Assert.Equal(new ImageGeometry(64, 32), plain);
        var rotated = EditStore.ReadGeometry(WriteJpeg("b.jpg", 64, 32, orientation: 6));
        Assert.Equal((32, 64, SKEncodedOrigin.RightTop, false), (rotated.Width, rotated.Height, rotated.Orientation, rotated.IsRaw));
    }

    [Fact]
    public void PasteToFile_UpdatesSidecars_NotThePhoto()
    {
        var photo = WriteJpeg("photo.jpg", 60, 40);
        var before = File.ReadAllBytes(photo);
        // The photo already has its own crop and contrast.
        SidecarFile.Save(photo, EditDocument.From(Target));

        var result = SettingsTransfer.PasteToFile(photo, Source, SettingsGroups.Light | SettingsGroups.Color);
        Assert.Null(result.Error);
        Assert.Equal(before, File.ReadAllBytes(photo));

        var saved = SidecarFile.Load(photo)!.ToState();
        Assert.Equal(1.2, saved.Adjustments.Exposure);
        Assert.Equal(15, saved.Adjustments.Temperature);
        Assert.Equal(Target.Crop, saved.Crop);                       // not copied
        Assert.Equal(new HslBand(5, 5, 5), saved.Adjustments.Reds);  // not copied
        Assert.True(File.Exists(LightroomXmp.PathFor(photo)));
        Assert.Equal(1.2, LightroomXmp.Load(photo, new ImageGeometry(60, 40))!.Adjustments.Exposure);
    }

    [Fact]
    public void PasteToFile_UnreadablePhoto_ReportsError()
    {
        var bad = Path.Combine(_dir, "bad.jpg");
        File.WriteAllText(bad, "not a photo");
        var result = SettingsTransfer.PasteToFile(bad, Source, SettingsGroups.Default);
        Assert.NotNull(result.Error);
        Assert.False(SidecarFile.Exists(bad));
    }
}
