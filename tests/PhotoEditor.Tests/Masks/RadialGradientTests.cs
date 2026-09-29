using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Tests.Masks;

public class RadialGradientTests
{
    private const int W = 200, H = 100;
    private const float Aspect = (float)W / H;

    private static float[] Render(MaskComponent c)
    {
        var cov = new float[W * H];
        c.Render(cov, W, H);
        return cov;
    }

    [Fact]
    public void EqualRadii_AreCircularInPixels()
    {
        // radius 0.1 × 200 px = 20 px in both directions, centred at (100, 50)
        var g = new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.1f, RadiusY = 0.1f, Feather = 0f };
        var cov = Render(g);
        Assert.Equal(1f, cov[50 * W + 100]);
        Assert.Equal(1f, cov[50 * W + 118]);
        Assert.Equal(0f, cov[50 * W + 122]);
        Assert.Equal(1f, cov[68 * W + 100]);
        Assert.Equal(0f, cov[72 * W + 100]);
    }

    [Fact]
    public void Feather_FadesTowardsEdge()
    {
        var g = new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.2f, RadiusY = 0.2f, Feather = 1f };
        float centre = g.CoverageAt(100, 50, W, H), mid = g.CoverageAt(120, 50, W, H), edge = g.CoverageAt(139, 50, W, H);
        Assert.Equal(1f, centre);
        Assert.InRange(mid, 0.3f, 0.7f);
        Assert.True(edge < 0.05f);
    }

    [Fact]
    public void Invert_CoversOutside()
    {
        var g = new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.1f, RadiusY = 0.1f, Feather = 0f, Invert = true };
        var cov = MaskRasterizer.Rasterize(new Mask { Components = [g] }, W, H);
        Assert.Equal(0f, cov[50 * W + 100]);
        Assert.Equal(1f, cov[0]);
    }

    [Fact]
    public void RenderMatchesCoverageAt()
    {
        var g = new RadialGradientComponent { Center = new(0.3f, 0.6f), RadiusX = 0.25f, RadiusY = 0.1f, Feather = 0.7f };
        Assert.Equal(g.CoverageAt(77.5f, 55.5f, W, H), Render(g)[55 * W + 77], 5);
    }

    [Fact]
    public void Handles_SitOnTheEllipse()
    {
        var g = new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.1f, RadiusY = 0.1f, Feather = 0f };
        foreach (var (handle, p) in g.HandlePoints(Aspect).Where(h => h.Handle != GradientHandle.Move))
        {
            // Just inside the handle towards the centre is covered, just outside is not.
            float px = p.X * W, py = p.Y * H, cx = g.Center.X * W, cy = g.Center.Y * H;
            float ux = (cx - px) / 20f, uy = (cy - py) / 20f;
            Assert.Equal(1f, g.CoverageAt(px + ux, py + uy, W, H));
            Assert.Equal(0f, g.CoverageAt(px - ux, py - uy, W, H));
        }
    }

    [Fact]
    public void DragHandles()
    {
        var g = new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0.1f, RadiusY = 0.1f };
        Assert.Equal(0.3f, g.DragHandle(GradientHandle.Right, new(0.6f, 0.5f), new(0.8f, 0.55f), Aspect).RadiusX, 5);
        // Dragging the bottom handle 0.2 of the height = 20 px = 0.1 of the width
        Assert.Equal(0.1f, g.DragHandle(GradientHandle.Bottom, new(0.5f, 0.7f), new(0.5f, 0.7f), Aspect).RadiusY, 5);
        var moved = g.DragHandle(GradientHandle.Move, new(0.5f, 0.5f), new(0.6f, 0.4f), Aspect);
        Assert.Equal(0.6f, moved.Center.X, 5);
        Assert.Equal(0.4f, moved.Center.Y, 5);
        var created = new RadialGradientComponent { Center = new(0.5f, 0.5f), RadiusX = 0, RadiusY = 0 }
            .DragHandle(GradientHandle.End, new(0.5f, 0.5f), new(0.7f, 0.9f), Aspect);
        Assert.Equal(0.2f, created.RadiusX, 5);
        Assert.Equal(0.2f, created.RadiusY, 5);
    }

    [Fact]
    public void Sidecar_RoundTrip()
    {
        var doc = new EditDocument
        {
            Masks = [new Mask { Components = [new RadialGradientComponent { Center = new(0.4f, 0.6f), RadiusX = 0.3f, Feather = 0.8f, Mode = MaskMode.Subtract }] }],
        };
        var json = SidecarFile.Serialize(doc);
        Assert.Contains("\"type\": \"radial\"", json);
        Assert.Equal(doc, SidecarFile.Deserialize(json));
    }
}
