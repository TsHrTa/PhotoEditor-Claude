using System.Collections.Immutable;
using System.Xml.Linq;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Presets;

namespace PhotoEditor.Tests.Adjustments;

/// <summary>The tone curve panel: parametric regions and point curves (math, rendering, sidecars, Lightroom XMP).</summary>
public class ToneCurvePanelTests
{
    private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private static readonly ImageGeometry Landscape = new(6000, 4000);

    public static PointCurve Curve(params (double X, double Y)[] points) =>
        new() { Points = [.. points.Select(p => new CurvePoint(p.X, p.Y))] };

    [Fact]
    public void Spline_PassesThroughItsPoints_AndIsSmooth()
    {
        var curve = Curve((0, 0), (0.25, 0.15), (0.5, 0.5), (0.75, 0.85), (1, 1));
        foreach (var p in curve.Points)
            Assert.Equal(p.Y, curve.Evaluate(p.X), 6);
        // Rising everywhere for a rising set of points, and no kinks: neighbouring slopes are close.
        double previous = curve.Evaluate(0), previousSlope = double.NaN;
        for (int i = 1; i <= 1000; i++)
        {
            double y = curve.Evaluate(i / 1000.0);
            double slope = (y - previous) * 1000;
            Assert.True(slope >= 0, $"falls at {i / 1000.0}");
            if (!double.IsNaN(previousSlope))
                Assert.True(Math.Abs(slope - previousSlope) < 0.05, $"kink at {i / 1000.0}");
            previous = y;
            previousSlope = slope;
        }
    }

    [Fact]
    public void Spline_TwoPoints_IsAStraightLine_AndEndsHoldOutside()
    {
        var matte = Curve((0, 0.1), (0.8, 0.9));
        Assert.Equal(0.5, matte.Evaluate(0.4), 6);
        Assert.Equal(0.9, matte.Evaluate(0.95), 6); // beyond the last point: its value
        Assert.Equal(0.1, Curve((0.1, 0.1), (1, 1)).Evaluate(0.05), 6);
    }

    [Fact]
    public void Normalized_SortsClampsAndMergesPoints()
    {
        var c = Curve((1, 1), (0.5, 0.7), (-0.2, 0), (0.5005, 0.6), (0.2, 1.4)).Normalized();
        Assert.Equal([new(0, 0), new(0.2, 1), new(0.5005, 0.6), new(1, 1)], c.Points);
        Assert.Same(PointCurve.Linear, Curve((0.3, 0.3)).Normalized());
        Assert.True(Curve((0, 0), (0.5, 0.5), (1, 1)).IsLinear);
        Assert.Equal(Curve((0, 0), (1, 1)), PointCurve.Linear); // compared by points
    }

    [Fact]
    public void NoCurve_HasNoTable()
    {
        Assert.Null(ToneCurveTable.For(AdjustmentSettings.Default));
        Assert.Null(ToneCurveTable.For(new AdjustmentSettings { CurveShadowSplit = 40, Curve = Curve((0, 0), (0.5, 0.5), (1, 1)) }));
        var s = new AdjustmentSettings { CurveLights = 20 };
        Assert.Same(ToneCurveTable.For(s), ToneCurveTable.For(s with { })); // cached for the same curve
    }

    [Theory]
    [InlineData(100, 0, 0, 0)]
    [InlineData(0, -100, 0, 0)]
    [InlineData(0, 100, -100, 0)]
    [InlineData(-100, 100, -100, 100)]
    public void Parametric_KeepsEndsAndRises(double highlights, double lights, double darks, double shadows)
    {
        var values = ToneCurveTable.Parametric(new AdjustmentSettings
        {
            CurveHighlights = highlights, CurveLights = lights, CurveDarks = darks, CurveShadows = shadows,
        });
        Assert.Equal(0, values[0], 6);
        Assert.Equal(1, values[^1], 6);
        for (int i = 1; i < values.Length; i++)
            Assert.True(values[i] >= values[i - 1]);
    }

    [Fact]
    public void Parametric_MovesItsOwnRegion()
    {
        double At(AdjustmentSettings s, double x) => ToneCurveTable.Parametric(s)[(int)Math.Round(x * (ToneCurveTable.Size - 1))];
        var shadows = new AdjustmentSettings { CurveShadows = 100 };
        Assert.True(At(shadows, 0.15) > 0.15 + 0.08);
        Assert.Equal(0.8, At(shadows, 0.8), 3); // highlights untouched
        var highlights = new AdjustmentSettings { CurveHighlights = -100 };
        Assert.True(At(highlights, 0.85) < 0.85 - 0.08);
        Assert.Equal(0.3, At(highlights, 0.3), 3);
        // Moving the highlight split up moves the highlights region with it.
        var narrow = highlights with { CurveHighlightSplit = 90 };
        Assert.True(At(narrow, 0.75) > At(highlights, 0.75));
    }

    [Fact]
    public void ChannelCurve_TintsTheImage()
    {
        using var src = TestImages.Solid(new SkiaSharp.SKColor(128, 128, 128));
        using var cpu = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { CurveBlue = Curve((0, 0), (0.5, 0.6), (1, 1)) });
        var c = cpu.GetPixel(1, 1);
        Assert.InRange(c.Red, 127, 129);
        Assert.InRange(c.Green, 127, 129);
        Assert.True(c.Blue > 140, $"blue {c.Blue}");
    }

    [Fact]
    public void CurveInAMask_IsIgnored_ShaderMatchesCpu()
    {
        using var src = TestImages.Varied();
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { CurveDarks = 40 },
            Masks =
            [
                new Mask
                {
                    Adjustments = new AdjustmentSettings { Exposure = 0.5, CurveHighlights = -100, Curve = Curve((0, 0.3), (1, 1)) },
                    Components = [new RadialGradientComponent()],
                },
            ],
        };
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
        var withoutMaskCurve = state with
        {
            Masks = [state.Masks[0] with { Adjustments = new AdjustmentSettings { Exposure = 0.5 } }],
        };
        using var expected = CpuAdjustmentRenderer.Render(src, withoutMaskCurve);
        Assert.Equal(0, TestImages.MaxDifference(expected, cpu, out _));
    }

    private static readonly AdjustmentSettings Sample = new()
    {
        CurveHighlights = -35, CurveLights = 20, CurveDarks = -10, CurveShadows = 45,
        CurveShadowSplit = 30, CurveMidtoneSplit = 55, CurveHighlightSplit = 80,
        Curve = Curve((0, 20 / 255.0), (64 / 255.0, 50 / 255.0), (192 / 255.0, 210 / 255.0), (1, 1)),
        CurveRed = Curve((0, 0), (128 / 255.0, 140 / 255.0), (1, 1)),
        CurveBlue = Curve((0, 0), (1, 240 / 255.0)),
    };

    [Fact]
    public void Xmp_RoundTrips_InLightroomsFormat()
    {
        var xml = LightroomXmp.Write(new EditState { Adjustments = Sample }, Landscape, null, out _);
        var d = XDocument.Parse(xml).Descendants().First(e => e.Attribute(Crs + "ParametricShadows") is not null);
        Assert.Equal("+45", d.Attribute(Crs + "ParametricShadows")!.Value);
        Assert.Equal("80", d.Attribute(Crs + "ParametricHighlightSplit")!.Value);
        Assert.Equal("Custom", d.Attribute(Crs + "ToneCurveName2012")!.Value);
        Assert.Equal(["0, 20", "64, 50", "192, 210", "255, 255"],
            d.Element(Crs + "ToneCurvePV2012")!.Descendants().Where(e => e.Name.LocalName == "li").Select(e => e.Value));
        Assert.Equal(["0, 0", "255, 255"],
            d.Element(Crs + "ToneCurvePV2012Green")!.Descendants().Where(e => e.Name.LocalName == "li").Select(e => e.Value));

        var read = LightroomXmp.Read(xml, Landscape).Adjustments;
        Assert.Equal(Sample, read);
    }

    [Fact]
    public void Xmp_Rewrite_ReplacesTheCurves()
    {
        var first = LightroomXmp.Write(new EditState { Adjustments = Sample }, Landscape, null, out _);
        var second = LightroomXmp.Write(EditState.Default, Landscape, first, out _);
        Assert.Single(XDocument.Parse(second).Descendants(Crs + "ToneCurvePV2012"));
        Assert.Equal(AdjustmentSettings.Default, LightroomXmp.Read(second, Landscape).Adjustments);
        Assert.Contains("ToneCurveName2012=\"Linear\"", second);
    }

    [Fact]
    public void Sidecar_RoundTrips_AndRepairsBadCurves()
    {
        var doc = EditDocument.From(new EditState { Adjustments = Sample });
        Assert.Equal(doc, SidecarFile.Deserialize(SidecarFile.Serialize(doc)));

        const string json = """
            { "adjustments": { "curve": { "points": [ { "x": 1, "y": 1 }, { "x": 0, "y": 0.2 } ] }, "curveRed": null,
                               "curveBlue": { "points": [] } } }
            """;
        var a = SidecarFile.Deserialize(json).Adjustments;
        Assert.Equal(Curve((0, 0.2), (1, 1)), a.Curve);
        Assert.Equal(PointCurve.Linear, a.CurveRed);
        Assert.Equal(PointCurve.Linear, a.CurveBlue);
    }

    [Fact]
    public void CopySettings_AndPresets_CarryTheCurves()
    {
        var source = new EditState { Adjustments = Sample };
        var pasted = SettingsTransfer.Apply(EditState.Default, source, SettingsGroups.ToneCurve, 600, 400).Adjustments;
        Assert.Equal(Sample, pasted);
        Assert.Equal(AdjustmentSettings.Default,
            SettingsTransfer.Apply(EditState.Default, source, SettingsGroups.All & ~SettingsGroups.ToneCurve, 600, 400).Adjustments);

        var preset = PresetFactory.FromEdit("curves", source, SettingsGroups.ToneCurve);
        var json = PresetStore.Serialize(preset);
        var loaded = PresetStore.Deserialize(json);
        Assert.Equal(preset.Steps.Count, loaded.Steps.Count);
        var applied = PresetEngine.Apply(loaded, EditState.Default, TestImages.Varied()).State.Adjustments;
        Assert.Equal(Sample, applied);
        // The Detail group no longer holds the curve sliders.
        Assert.DoesNotContain("curveLights", PresetFactory.SliderIds(SettingsGroups.Detail));
    }
}
