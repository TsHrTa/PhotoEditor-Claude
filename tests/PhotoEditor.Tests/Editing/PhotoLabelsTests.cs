using System.Xml.Linq;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Library;

namespace PhotoEditor.Tests.Editing;

public sealed class PhotoLabelsTests : IDisposable
{
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private readonly string _dir = Directory.CreateTempSubdirectory("labels-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Photo(string name = "IMG_0001.CR3")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static readonly ImageGeometry Geometry = new(6000, 4000, IsRaw: true);

    private static string? XmpRating(string photo) =>
        XDocument.Load(LightroomXmp.PathFor(photo)).Descendants().Select(e => e.Attribute(Xmp + "Rating")?.Value).FirstOrDefault(v => v is not null);

    [Fact]
    public void Labels_RoundTrip_AndGoIntoTheXmpRating()
    {
        var photo = Photo();
        Assert.Equal(PhotoLabels.None, EditStore.LoadLabels(photo));
        EditStore.SaveLabels(photo, new PhotoLabels(4, PhotoFlag.Pick));
        Assert.Equal(new PhotoLabels(4, PhotoFlag.Pick), EditStore.LoadLabels(photo));
        Assert.Equal("4", XmpRating(photo));
        Assert.Contains("\"pick\"", File.ReadAllText(SidecarFile.PathFor(photo)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isDefault", File.ReadAllText(SidecarFile.PathFor(photo)));

        EditStore.SaveLabels(photo, new PhotoLabels(4, PhotoFlag.Reject));
        Assert.Equal("-1", XmpRating(photo)); // Adobe's "rejected"
        EditStore.SaveLabels(photo, PhotoLabels.None);
        Assert.Null(XmpRating(photo));
    }

    [Fact]
    public void LabelsOnly_IsNotAnEdit()
    {
        var photo = Photo();
        EditStore.SaveLabels(photo, new PhotoLabels(3));
        Assert.False(PhotoFolder.HasEdits(photo));
        Assert.Equal(EditSource.None, EditStore.Load(photo, Geometry).Source);
    }

    [Fact]
    public void SavingTheEdit_KeepsTheLabels_AndSavingLabels_KeepsTheEdit()
    {
        var photo = Photo();
        var edit = new EditState { Adjustments = new AdjustmentSettings { Exposure = 0.7 } };
        EditStore.Save(photo, edit, Geometry);
        EditStore.SaveLabels(photo, new PhotoLabels(5));
        Assert.Equal(0.7, EditStore.Load(photo, Geometry).State.Adjustments.Exposure);
        Assert.True(PhotoFolder.HasEdits(photo));

        EditStore.Save(photo, edit with { Adjustments = new AdjustmentSettings { Exposure = -1 } }, Geometry);
        Assert.Equal(new PhotoLabels(5), EditStore.LoadLabels(photo));
        Assert.Equal("5", XmpRating(photo)); // the edit's XMP write merges into the file and keeps the rating
        Assert.Equal("-1.00", XDocument.Load(LightroomXmp.PathFor(photo)).Descendants()
            .Select(e => e.Attribute((XNamespace)"http://ns.adobe.com/camera-raw-settings/1.0/" + "Exposure2012")?.Value)
            .First(v => v is not null));
    }

    [Fact]
    public void RatingFromLightroom_IsReadWhenThereIsNoJson()
    {
        var photo = Photo();
        File.WriteAllText(LightroomXmp.PathFor(photo), """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
             <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="2" />
             </rdf:RDF>
            </x:xmpmeta>
            """);
        Assert.Equal(new PhotoLabels(2), EditStore.LoadLabels(photo));
        Assert.False(PhotoFolder.HasEdits(photo));

        File.WriteAllText(LightroomXmp.PathFor(photo), File.ReadAllText(LightroomXmp.PathFor(photo)).Replace("\"2\"", "\"-1\""));
        Assert.Equal(new PhotoLabels(0, PhotoFlag.Reject), EditStore.LoadLabels(photo));
    }

    [Theory]
    [InlineData(LabelFilter.All, 0, PhotoFlag.Reject, true)]
    [InlineData(LabelFilter.Picked, 3, PhotoFlag.None, false)]
    [InlineData(LabelFilter.Picked, 0, PhotoFlag.Pick, true)]
    [InlineData(LabelFilter.NotRejected, 0, PhotoFlag.Reject, false)]
    [InlineData(LabelFilter.ThreeStarsPlus, 3, PhotoFlag.None, true)]
    [InlineData(LabelFilter.ThreeStarsPlus, 2, PhotoFlag.Pick, false)]
    [InlineData(LabelFilter.Unrated, 0, PhotoFlag.Pick, true)]
    public void Filters(LabelFilter filter, int rating, PhotoFlag flag, bool expected) =>
        Assert.Equal(expected, filter.Matches(new PhotoLabels(rating, flag)));
}
