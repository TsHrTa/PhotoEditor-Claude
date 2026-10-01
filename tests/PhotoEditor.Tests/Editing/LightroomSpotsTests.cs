using System.Xml.Linq;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;
using SkiaSharp;

namespace PhotoEditor.Tests.Editing;

/// <summary>Spot removal in Lightroom's XMP, checked against a sidecar Lightroom Classic 9.2 wrote.</summary>
public class LightroomSpotsTests
{
    private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";

    /// <summary>The Canon R8 CR3 of the sample: 6000 × 4000 sensor, orientation 6 (portrait, turned right).</summary>
    private static readonly ImageGeometry Portrait = new(4000, 6000, SKEncodedOrigin.RightTop, IsRaw: true);

    /// <summary>
    /// Lightroom's RetouchAreas from the sample (the Content-Aware Remove's patch blobs shortened): a heal, a clone
    /// with a moved source, a heal at 50 % opacity, and a Content-Aware Remove.
    /// </summary>
    private const string Sample = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about="" xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
            crs:HasSettings="True" crs:Exposure2012="+1.02" crs:Table_615E5F704C1253F48EE953CA8FA80B53="blob">
           <crs:RetouchAreas>
            <rdf:Seq>
             <rdf:li><rdf:Description crs:SpotType="heal" crs:SourceState="sourceSetExplicitly" crs:Method="gaussian"
               crs:HealVersion="2" crs:SourceX="0.169206" crs:OffsetY="0.834132" crs:Opacity="1" crs:Feather="0" crs:Seed="2">
              <crs:Masks><rdf:Seq><rdf:li crs:What="Mask/Ellipse" crs:MaskActive="true" crs:MaskBlendMode="0" crs:MaskInverted="false"
               crs:MaskSyncID="9D6FBE034A06694AB83CF79C5CE59B02" crs:MaskValue="1" crs:X="0.117048" crs:Y="0.819672"
               crs:SizeX="0.008102" crs:SizeY="0.008102" crs:Alpha="0" crs:CenterValue="1" crs:PerimeterValue="0"/></rdf:Seq></crs:Masks>
             </rdf:Description></rdf:li>
             <rdf:li><rdf:Description crs:SpotType="clone" crs:SourceState="sourceSetExplicitly" crs:Method="gaussian"
               crs:HealVersion="2" crs:SourceX="0.034422" crs:OffsetY="0.238763" crs:Opacity="1" crs:Feather="0" crs:Seed="2">
              <crs:Masks><rdf:Seq><rdf:li crs:What="Mask/Ellipse" crs:MaskSyncID="D1EE078E6F695144AC145FBFCB8FC8FF"
               crs:X="0.072259" crs:Y="0.026462" crs:SizeX="0.007948" crs:SizeY="0.007948"/></rdf:Seq></crs:Masks>
             </rdf:Description></rdf:li>
             <rdf:li><rdf:Description crs:SpotType="heal" crs:SourceX="0.296267" crs:OffsetY="0.804359" crs:Opacity="0.501166" crs:Feather="0">
              <crs:Masks><rdf:Seq><rdf:li crs:What="Mask/Ellipse" crs:MaskSyncID="877D9D2FE1A5584C89776AFF22979A44"
               crs:X="0.238979" crs:Y="0.899006" crs:SizeX="0.008214" crs:SizeY="0.008214"/></rdf:Seq></crs:Masks>
             </rdf:Description></rdf:li>
             <rdf:li><rdf:Description crs:SpotType="heal_patchmatch" crs:SourceState="sourceAutoComputed" crs:Opacity="1"
               crs:pm_patch="615E5F704C1253F48EE953CA8FA80B53" crs:fill_method="firefly">
              <crs:Masks><rdf:Seq><rdf:li crs:What="Mask/Ellipse" crs:MaskSyncID="0F3445D0AC4DB0498A8C4B0DE1A15A5C"
               crs:X="0.733548" crs:Y="0.055781" crs:SizeX="0.034455" crs:SizeY="0.034455"/></rdf:Seq></crs:Masks>
             </rdf:Description></rdf:li>
            </rdf:Seq>
           </crs:RetouchAreas>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;

    [Fact]
    public void Read_LightroomSample_PlacesTheSpotsWhereLightroomShowsThem()
    {
        var spots = LightroomXmp.Read(Sample, Portrait).Spots;
        Assert.Equal(4, spots.Count);
        Assert.Equal([SpotMode.Heal, SpotMode.Clone, SpotMode.Heal, SpotMode.Remove], spots.Select(s => s.Mode));

        // Upright (as in Lightroom's screenshot of the portrait photo): x = 1 − sensor y, y = sensor x.
        var heal = spots[0];
        Assert.Equal(1 - 0.819672, heal.Center.X, 4);
        Assert.Equal(0.117048, heal.Center.Y, 4);
        Assert.Equal(1 - 0.834132, heal.Source.X, 4);
        Assert.Equal(0.169206, heal.Source.Y, 4);
        Assert.Equal(0.008102f, heal.Radius, 5);
        Assert.Equal(Guid.Parse("9D6FBE034A06694AB83CF79C5CE59B02"), heal.Id);
        Assert.Equal(0.501166f, spots[2].Opacity, 4);
        Assert.Equal(0f, heal.Feather);

        // The Content-Aware Remove: the big circle near the right edge, 3/4 down; radius ≈ 207 px of the 6000 px side.
        var remove = spots[3];
        Assert.Equal(1 - 0.055781, remove.Center.X, 4);
        Assert.Equal(0.733548, remove.Center.Y, 4);
        Assert.Equal(207, remove.Radius * 6000, 0);
        Assert.Null(remove.Fill);
        Assert.Single(remove.Path);
    }

    [Fact]
    public void WriteThenRead_RoundTripsHealAndClone_AndKeepsLightroomsContentAwareRemove()
    {
        var state = LightroomXmp.Read(Sample, Portrait);
        var moved = state with
        {
            Spots = state.Spots.SetItem(1, state.Spots[1] with { Center = new BrushPoint(0.5f, 0.25f), Feather = 0.4f }),
        };
        var xml = LightroomXmp.Write(moved, Portrait, Sample, out var skipped);
        Assert.DoesNotContain(skipped, s => s.Contains("spot"));
        var back = LightroomXmp.Read(xml, Portrait).Spots;
        Assert.Equal(4, back.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(moved.Spots[i].Id, back[i].Id);
            Assert.Equal(moved.Spots[i].Mode, back[i].Mode);
            Assert.Equal(moved.Spots[i].Center.X, back[i].Center.X, 5);
            Assert.Equal(moved.Spots[i].Center.Y, back[i].Center.Y, 5);
            Assert.Equal(moved.Spots[i].Radius, back[i].Radius, 5);
            Assert.Equal(moved.Spots[i].Opacity, back[i].Opacity, 4);
        }
        Assert.Equal(0.4f, back[1].Feather, 4);

        // Lightroom's own Content-Aware Remove stays byte for byte, and its patch blob stays on the description.
        var doc = XDocument.Parse(xml);
        var patchmatch = doc.Descendants().Single(e => (string?)e.Attribute(Crs + "SpotType") == "heal_patchmatch");
        Assert.Equal("firefly", (string?)patchmatch.Attribute(Crs + "fill_method"));
        Assert.Contains("Table_615E5F704C1253F48EE953CA8FA80B53", xml);
        // The older text form, for readers that only know it.
        var info = doc.Descendants(Crs + "RetouchInfo").Single().Descendants().Where(e => e.Name.LocalName == "li").ToList();
        Assert.Equal(3, info.Count);
        Assert.StartsWith("centerX = 0.117048, centerY = 0.819672, radius = 0.008102", info[0].Value);
        Assert.EndsWith("spotType = heal, opacity = 0.5012", info[2].Value);
    }

    [Fact]
    public void Write_DeletedContentAwareRemove_IsDropped_AndOwnAiRemoveIsReported()
    {
        var state = LightroomXmp.Read(Sample, Portrait);
        var edited = state with
        {
            Spots = state.Spots.RemoveAt(3).Add(new Spot { Mode = SpotMode.Remove, Path = [new BrushPoint(0.2f, 0.2f)], Fill = "abc" }),
        };
        var xml = LightroomXmp.Write(edited, Portrait, Sample, out var skipped);
        Assert.DoesNotContain("heal_patchmatch", xml);
        Assert.Contains(skipped, s => s.Contains("Remove"));
        Assert.Equal(3, LightroomXmp.Read(xml, Portrait).Spots.Count);
    }

    [Fact]
    public void Read_OldRetouchInfo_WhenThereAreNoRetouchAreas()
    {
        const string xml = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
             <rdf:Description xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/" crs:HasSettings="True">
              <crs:RetouchInfo><rdf:Seq>
               <rdf:li>centerX = 0.25, centerY = 0.5, radius = 0.01, sourceState = sourceSetExplicitly, sourceX = 0.3, sourceY = 0.5, spotType = clone, opacity = 0.5</rdf:li>
              </rdf:Seq></crs:RetouchInfo>
             </rdf:Description></rdf:RDF></x:xmpmeta>
            """;
        var spot = Assert.Single(LightroomXmp.Read(xml, new ImageGeometry(6000, 4000)).Spots);
        Assert.Equal((SpotMode.Clone, 0.25f, 0.5f, 0.3f, 0.01f, 0.5f),
            (spot.Mode, spot.Center.X, spot.Center.Y, spot.Source.X, spot.Radius, spot.Opacity));
    }
}
