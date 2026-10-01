using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Library;

namespace PhotoEditor.Tests.Editing;

/// <summary>RAWs start with Lightroom's default sharpening and colour noise reduction; other photos start at zero.</summary>
public sealed class RawDefaultsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rawdefaults-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Photo(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static readonly ImageGeometry Raw = new(6000, 4000, IsRaw: true);
    private static readonly ImageGeometry Jpeg = new(6000, 4000);

    [Fact]
    public void UneditedRaw_StartsWithLightroomDefaults_JpegAtZero()
    {
        var (raw, source) = EditStore.Load(Photo("IMG_1.CR3"), Raw);
        Assert.Equal(EditSource.None, source);
        Assert.Equal(40, raw.Adjustments.SharpenAmount);
        Assert.Equal(1, raw.Adjustments.SharpenRadius);
        Assert.Equal(0, raw.Adjustments.SharpenMasking);
        Assert.Equal(25, raw.Adjustments.NoiseColor);
        Assert.Equal(0, raw.Adjustments.NoiseLuminance);
        Assert.True(raw.IsDefaultFor(isRaw: true));

        var (jpeg, _) = EditStore.Load(Photo("IMG_1.JPG"), Jpeg);
        Assert.Equal(EditState.Default, jpeg);
    }

    [Fact]
    public void SavingTheRawDefaults_WritesNoSidecar()
    {
        var photo = Photo("IMG_2.CR3");
        EditStore.Save(photo, EditState.RawDefault, Raw);
        Assert.False(SidecarFile.Exists(photo));
        Assert.False(File.Exists(LightroomXmp.PathFor(photo)));
        Assert.False(PhotoFolder.HasEdits(photo));
    }

    [Fact]
    public void TurningTheRawDefaultsOff_IsAnEditThatSticks()
    {
        var photo = Photo("IMG_3.CR3");
        EditStore.Save(photo, EditState.Default, Raw); // sharpening and noise reduction set to 0
        Assert.True(SidecarFile.Exists(photo));
        Assert.True(PhotoFolder.HasEdits(photo));
        var (state, source) = EditStore.Load(photo, Raw);
        Assert.Equal(EditSource.Json, source);
        Assert.Equal(EditState.Default, state);

        // Resetting back to the defaults: the file stays (it existed) but holds no edit.
        EditStore.Save(photo, EditState.RawDefault, Raw);
        Assert.False(PhotoFolder.HasEdits(photo));
        Assert.Equal(EditState.RawDefault, EditStore.Load(photo, Raw).State);
    }

    [Fact]
    public void RatingOnly_RawStillGetsItsDefaults()
    {
        var photo = Photo("IMG_4.CR3");
        EditStore.SaveLabels(photo, new PhotoLabels(3));
        Assert.False(PhotoFolder.HasEdits(photo));
        Assert.Equal((EditState.RawDefault, EditSource.None), EditStore.Load(photo, Raw));
    }

    [Fact]
    public void OlderSidecar_WithAnEdit_KeepsItsOwnSharpening()
    {
        // Written before RAW defaults existed: no "edited" flag, sharpening 0, exposure changed.
        var photo = Photo("IMG_5.CR3");
        var old = new EditState { Adjustments = AdjustmentSettings.Default with { Exposure = 1 } };
        SidecarFile.Save(photo, EditDocument.From(old));
        Assert.Equal(old, EditStore.Load(photo, Raw).State);
    }

    [Fact]
    public void Xmp_MissingDetailValues_AreCameraRawDefaults()
    {
        const string xml = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/" crs:Exposure2012="+0.50"/>
            </rdf:RDF></x:xmpmeta>
            """;
        var raw = LightroomXmp.Read(xml, Raw).Adjustments;
        Assert.Equal((40, 25), (raw.SharpenAmount, raw.NoiseColor));
        var jpeg = LightroomXmp.Read(xml, Jpeg).Adjustments;
        Assert.Equal((0, 0), (jpeg.SharpenAmount, jpeg.NoiseColor));
    }

    [Fact]
    public void XmpWritten_ForARaw_KeepsZeroSharpening()
    {
        var photo = Photo("IMG_6.CR3");
        EditStore.Save(photo, EditState.Default, Raw);
        File.Delete(SidecarFile.PathFor(photo)); // only Lightroom's file left
        Assert.Equal((EditState.Default, EditSource.Xmp), EditStore.Load(photo, Raw));
    }
}
