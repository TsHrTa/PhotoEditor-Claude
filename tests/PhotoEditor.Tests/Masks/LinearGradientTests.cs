using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Tests.Masks;

public class LinearGradientTests
{
    private const int W = 200, H = 100;

    private static float[] Render(MaskComponent c)
    {
        var cov = new float[W * H];
        c.Render(cov, W, H);
        return cov;
    }

    [Fact]
    public void TopToBottom_FullAboveStart_NoneBelowEnd()
    {
        var g = new LinearGradientComponent { Start = new(0.5f, 0.2f), End = new(0.5f, 0.6f) };
        var cov = Render(g);
        Assert.Equal(1f, cov[10 * W + 50]);        // y=10 above start (20)
        Assert.Equal(0f, cov[80 * W + 150]);       // y=80 below end (60)
        Assert.Equal(0.5f, cov[40 * W + 7], 1);    // midway (y≈40)
        for (int y = 1; y < H; y++)
            Assert.True(cov[y * W] <= cov[(y - 1) * W] + 1e-6f);
    }

    [Fact]
    public void Diagonal_IsPerpendicularInPixelSpace()
    {
        // 45° in pixels: from (50,25)px to (100,75)px. Points on a line perpendicular to it share coverage.
        var g = new LinearGradientComponent { Start = new(0.25f, 0.25f), End = new(0.5f, 0.75f) };
        float a = g.CoverageAt(80, 50, W, H);
        float b = g.CoverageAt(90, 40, W, H); // (+10, -10) is perpendicular to (1,1)
        Assert.Equal(a, b, 5);
    }

    [Fact]
    public void RenderMatchesCoverageAt()
    {
        var g = new LinearGradientComponent { Start = new(0.1f, 0.9f), End = new(0.7f, 0.3f) };
        var cov = Render(g);
        Assert.Equal(g.CoverageAt(123.5f, 45.5f, W, H), cov[45 * W + 123], 5);
    }

    [Fact]
    public void ZeroLength_IsEmpty() =>
        Assert.All(Render(new LinearGradientComponent { Start = new(0.5f, 0.5f), End = new(0.5f, 0.5f) }), v => Assert.Equal(0f, v));

    [Fact]
    public void Sidecar_RoundTrip()
    {
        var doc = new EditDocument
        {
            Masks = [new Mask { Components = [new LinearGradientComponent { Start = new(0.1f, 0.2f), End = new(0.3f, 0.4f), Invert = true }] }],
        };
        var json = SidecarFile.Serialize(doc);
        Assert.Contains("\"type\": \"linear\"", json);
        Assert.Equal(doc, SidecarFile.Deserialize(json));
    }
}

public class LinearGradientHandleTests
{
    private static readonly LinearGradientComponent G = new() { Start = new(0.2f, 0.2f), End = new(0.4f, 0.6f) };

    [Fact]
    public void DragStart_MovesOnlyStart()
    {
        var g = G.DragHandle(GradientHandle.Start, new(0.2f, 0.2f), new(0.25f, 0.1f));
        Assert.Equal(new BrushPoint(0.25f, 0.1f), g.Start);
        Assert.Equal(G.End, g.End);
    }

    [Fact]
    public void DragEnd_ByOffset()
    {
        var g = G.DragHandle(GradientHandle.End, new(0.41f, 0.61f), new(0.51f, 0.71f));
        Assert.Equal(0.5f, g.End.X, 5);
        Assert.Equal(0.7f, g.End.Y, 5);
    }

    [Fact]
    public void Move_TranslatesBoth()
    {
        var g = G.DragHandle(GradientHandle.Move, new(0.3f, 0.4f), new(0.4f, 0.3f));
        Assert.Equal(0.3f, g.Start.X, 5);
        Assert.Equal(0.1f, g.Start.Y, 5);
        Assert.Equal(0.5f, g.End.X, 5);
        Assert.Equal(0.5f, g.End.Y, 5);
        Assert.Equal(new BrushPoint(0.4f, 0.3f), g.Center);
    }
}
