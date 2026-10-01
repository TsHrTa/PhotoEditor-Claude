using System.Diagnostics;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Tests.Masks;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>Shader render time with several masks (the shader chain must grow linearly with the masks).</summary>
public sealed class MaskChainBench(ITestOutputHelper output)
{
    /// <summary>
    /// Mask passes nest the previous passes as the `image` child; GPU compilers inline child calls, so the shader
    /// may evaluate `image` only once (more calls would multiply the program's size with each mask).
    /// </summary>
    [Fact]
    public void Shader_EvaluatesThePreviousPassOnlyOnce()
    {
        int calls = System.Text.RegularExpressions.Regex.Matches(AdjustmentShader.Source, @"\bimage\.eval\(").Count;
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RenderTime_GrowsLinearlyWithMasks()
    {
        using var src = TestImages.Varied(96, 96);
        var times = new List<double>();
        for (int n = 0; n <= 3; n++)
        {
            var state = new EditState
            {
                Masks = [.. Enumerable.Range(0, n).Select(i => new Mask
                {
                    Components = [new RectComponent(0.1f * i, 0, 0.5f + 0.1f * i, 1)],
                    Adjustments = new AdjustmentSettings { Exposure = 0.3 },
                })],
            };
            using (AdjustmentShader.RenderRaster(src, state)) { } // compile
            var w = Stopwatch.StartNew();
            using (AdjustmentShader.RenderRaster(src, state)) { }
            times.Add(w.Elapsed.TotalMilliseconds);
            output.WriteLine($"{n} masks: {w.Elapsed.TotalMilliseconds:0.0} ms");
        }
        // Each mask adds one pass: 3 masks must not cost more than ~16× no mask (a blow-up per mask would be hundreds).
        Assert.True(times[3] < Math.Max(times[0], 1) * 16, string.Join(", ", times.Select(t => t.ToString("0.0"))));
    }
}
