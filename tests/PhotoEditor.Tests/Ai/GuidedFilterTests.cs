using PhotoEditor.Core.Ai;

namespace PhotoEditor.Tests.Ai;

public class GuidedFilterTests
{
    [Fact]
    public void BoxMean_AveragesTheWindow()
    {
        float[] v = [1, 2, 3, 4];
        var m = GuidedFilter.BoxMean(v, 4, 1, 1);
        Assert.Equal(1.5f, m[0], 5);
        Assert.Equal(2f, m[1], 5);
        Assert.Equal(3.5f, m[3], 5);
    }

    [Fact]
    public void Apply_SnapsABlurryMaskToTheImageEdge()
    {
        const int w = 40, h = 20;
        var guide = new float[w * h];
        var coarse = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            guide[y * w + x] = x < 20 ? 0.8f : 0.2f;                    // sharp edge at x = 20
            coarse[y * w + x] = Math.Clamp((30 - x) / 20f, 0f, 1f);      // mask fades over x = 10..30
        }
        var refined = GuidedFilter.Apply(coarse, guide, w, h, radius: 6, epsilon: 1e-4f);
        int row = 10 * w;
        // The coarse mask changes by 0.05 per pixel; the refined one jumps at the photo's edge.
        float coarseStep = coarse[row + 19] - coarse[row + 20];
        float refinedStep = refined[row + 19] - refined[row + 20];
        Assert.True(refinedStep > 5 * coarseStep, $"step at the edge: {refinedStep} vs {coarseStep}");
        Assert.True(refined[row + 19] > coarse[row + 19] && refined[row + 21] < coarse[row + 21]);
    }
}
