using PhotoEditor.Core.Adjustments;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Adjustments;

/// <summary>
/// Every Light slider must behave like a dial: more of it never makes the picture move backwards, and a tone curve
/// never inverts the order of tones (no plateaus, no reversals, however far the slider goes).
/// </summary>
public sealed class SliderResponseTests(ITestOutputHelper output)
{
    private static float Luma(in PreparedAdjustments p, float input)
    {
        float r = input, g = input, b = input;
        CpuAdjustmentRenderer.ApplyLinear(ref r, ref g, ref b, p);
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    private static AdjustmentSettings With(string slider, double value) => slider switch
    {
        "Exposure" => AdjustmentSettings.Default with { Exposure = value / 20 },
        "Contrast" => AdjustmentSettings.Default with { Contrast = value },
        "Highlights" => AdjustmentSettings.Default with { Highlights = value },
        "Shadows" => AdjustmentSettings.Default with { Shadows = value },
        "Whites" => AdjustmentSettings.Default with { Whites = value },
        "Blacks" => AdjustmentSettings.Default with { Blacks = value },
        _ => throw new ArgumentException(slider),
    };

    public static TheoryData<string> Sliders => new() { "Exposure", "Contrast", "Highlights", "Shadows", "Whites", "Blacks" };

    [Theory]
    [MemberData(nameof(Sliders))]
    public void TonesKeepTheirOrder_ForEverySliderValue(string slider)
    {
        for (double v = -200; v <= 200; v += 20)
        {
            var p = PreparedAdjustments.From(With(slider, v));
            float previous = -1f;
            for (int i = 1; i <= 400; i++)
            {
                float x = MathF.Pow(i / 400f, 3f) * 1.5f; // dense in the shadows, up to 1.5 above white
                float y = Luma(p, x);
                Assert.True(y >= previous - 1e-5f, $"{slider} {v}: output falls from {previous:0.0000} to {y:0.0000} at input {x:0.0000}");
                previous = y;
            }
        }
    }

    [Theory]
    [InlineData("Exposure", true)]
    [InlineData("Shadows", true)]
    [InlineData("Whites", true)]
    [InlineData("Blacks", true)]
    [InlineData("Highlights", true)]
    public void MoreOfTheSlider_NeverDarkensAPixel(string slider, bool brightens)
    {
        foreach (float x in new[] { 0.002f, 0.01f, 0.03f, 0.1f, 0.2f, 0.4f, 0.6f, 0.8f, 0.95f })
        {
            float previous = -1f;
            for (double v = -200; v <= 200; v += 10)
            {
                float y = Luma(PreparedAdjustments.From(With(slider, v)), x);
                Assert.True(y >= previous - 1e-5f, $"{slider} at input {x}: {v} gives {y:0.0000}, less than the {previous:0.0000} before");
                previous = y;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sliders))]
    public void TheResponseHasNoLongFlatPlateau(string slider)
    {
        // The output may flatten at the ends of the range (a clipped slider), but not stay put over most of the slider.
        foreach (float x in new[] { 0.01f, 0.05f, 0.2f, 0.5f, 0.8f })
        {
            var ys = Enumerable.Range(-20, 41).Select(i => Luma(PreparedAdjustments.From(With(slider, i * 10)), x)).ToArray();
            int flat = 0;
            for (int i = 1; i < ys.Length; i++)
                if (Math.Abs(ys[i] - ys[i - 1]) < 1e-6f)
                    flat++;
            output.WriteLine($"{slider} input {x}: {flat} of 40 steps flat");
            // A slider has no business in the part of the range it is not for: Whites does not touch deep shadows,
            // Blacks not highlights, Shadows not near white.
            bool outOfReach = (slider == "Whites" && x < 0.1f) || (slider == "Blacks" && x > 0.5f) || (slider == "Shadows" && x > 0.8f)
                || (slider == "Highlights" && x < 0.1f);
            Assert.True(flat <= 20 || outOfReach || slider == "Contrast", $"{slider} input {x}: output does not change over {flat} of 40 slider steps");
        }
    }
}
