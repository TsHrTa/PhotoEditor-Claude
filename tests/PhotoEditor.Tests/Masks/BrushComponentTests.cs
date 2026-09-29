using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Tests.Masks;

public class BrushComponentTests
{
    private const int W = 100, H = 50;

    private static float[] Render(BrushComponent brush)
    {
        var cov = new float[W * H];
        brush.Render(cov, W, H);
        return cov;
    }

    private static float At(float[] cov, int x, int y) => cov[y * W + x];

    private static BrushStroke Dot(float x, float y, float radius = 0.1f, float feather = 0f, float flow = 1f, bool erase = false) =>
        new() { Radius = radius, Feather = feather, Flow = flow, Erase = erase, Points = [new BrushPoint(x, y)] };

    [Fact]
    public void SingleDab_CoversCircleOfRadiusRelativeToLongSide()
    {
        // radius 0.1 × 100 px = 10 px around (50, 25)
        var cov = Render(new BrushComponent().AddStroke(Dot(0.5f, 0.5f)));
        Assert.Equal(1f, At(cov, 50, 25));
        Assert.Equal(1f, At(cov, 58, 25));
        Assert.Equal(0f, At(cov, 62, 25));
        Assert.Equal(1f, At(cov, 50, 33));   // isotropic despite the 2:1 aspect
        Assert.Equal(0f, At(cov, 50, 37));
    }

    [Fact]
    public void Feather_SoftensEdge()
    {
        var cov = Render(new BrushComponent().AddStroke(Dot(0.5f, 0.5f, feather: 1f)));
        float centre = At(cov, 50, 25), mid = At(cov, 55, 25), edge = At(cov, 59, 25);
        Assert.True(centre > 0.95f);
        Assert.InRange(mid, 0.3f, 0.7f);
        Assert.True(edge < 0.1f);
    }

    [Fact]
    public void Falloff_IsMonotonic()
    {
        float prev = 1f;
        for (float d = 0; d <= 1.01f; d += 0.01f)
        {
            float f = BrushComponent.Falloff(d, 0.6f);
            Assert.True(f <= prev + 1e-6f);
            prev = f;
        }
    }

    [Fact]
    public void Flow_BuildsUpWithRepeatedStrokes()
    {
        var once = new BrushComponent().AddStroke(Dot(0.5f, 0.5f, flow: 0.5f));
        var twice = once.AddStroke(Dot(0.5f, 0.5f, flow: 0.5f));
        Assert.Equal(0.5f, At(Render(once), 50, 25), 4);
        Assert.Equal(0.75f, At(Render(twice), 50, 25), 4);
    }

    [Fact]
    public void OverlappingDabsInOneStroke_DoNotBuildUp()
    {
        var stroke = Dot(0.3f, 0.5f, flow: 0.5f) with { Points = [new(0.3f, 0.5f), new(0.7f, 0.5f)] };
        var cov = Render(new BrushComponent().AddStroke(stroke));
        Assert.Equal(0.5f, At(cov, 50, 25), 4);
        Assert.Equal(0.5f, At(cov, 30, 25), 4);
    }

    [Fact]
    public void Line_IsContinuous()
    {
        var stroke = Dot(0.1f, 0.5f, radius: 0.02f) with { Points = [new(0.1f, 0.5f), new(0.9f, 0.5f)] };
        var cov = Render(new BrushComponent().AddStroke(stroke));
        for (int x = 10; x < 90; x++)
            Assert.Equal(1f, At(cov, x, 25));
    }

    [Fact]
    public void Erase_RemovesCoverage()
    {
        var brush = new BrushComponent()
            .AddStroke(Dot(0.5f, 0.5f))
            .AddStroke(Dot(0.5f, 0.5f, radius: 0.05f, erase: true));
        var cov = Render(brush);
        Assert.Equal(0f, At(cov, 50, 25));
        Assert.Equal(1f, At(cov, 58, 25));
    }

    [Fact]
    public void ExtendLastStroke_AddsPoint()
    {
        var brush = new BrushComponent().AddStroke(Dot(0.1f, 0.1f)).ExtendLastStroke(new BrushPoint(0.2f, 0.2f));
        Assert.Equal(2, brush.Strokes[0].Points.Count);
    }

    [Fact]
    public void Equality_ComparesStrokeContents()
    {
        var a = new BrushComponent().AddStroke(Dot(0.1f, 0.1f));
        var b = new BrushComponent().AddStroke(Dot(0.1f, 0.1f));
        Assert.Equal(a, b);
        Assert.NotEqual(a, b.ExtendLastStroke(new BrushPoint(0.3f, 0.3f)));
        Assert.NotEqual<MaskComponent>(a, a with { Mode = MaskMode.Subtract });
    }

    [Fact]
    public void Sidecar_RoundTripsBrushMask()
    {
        var brush = new BrushComponent { Invert = true }.AddStroke(Dot(0.25f, 0.75f, 0.03f, 0.4f, 0.8f))
            .ExtendLastStroke(new BrushPoint(0.3f, 0.7f));
        var doc = new EditDocument { Masks = [new Mask { Name = "Face", Components = [brush] }] };
        var json = SidecarFile.Serialize(doc);
        Assert.Contains("\"type\": \"brush\"", json);
        Assert.Equal(doc, SidecarFile.Deserialize(json));
    }
}
