using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.Tests.Adjustments;

public sealed class SaturationGamutTests
{
    private static float Hue(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d <= 0) return 0;
        float h = max == r ? 60 * ((g - b) / d) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return h < 0 ? h + 360 : h;
    }

    [Fact]
    public void A_mild_boost_is_applied_in_full()
    {
        float r = 0.4f, g = 0.3f, b = 0.25f;
        float y = ToneCurve.Luminance(r, g, b);
        SaturationMath.Apply(ref r, ref g, ref b, 1.3f);
        Assert.Equal(y + (0.4f - y) * 1.3f, r, 5);
        Assert.Equal(y + (0.25f - y) * 1.3f, b, 5);
    }

    [Fact]
    public void Reducing_saturation_is_never_limited()
    {
        float r = 0.9f, g = 0.05f, b = 0.01f;
        float y = ToneCurve.Luminance(r, g, b);
        SaturationMath.Apply(ref r, ref g, ref b, 0.3f);
        Assert.Equal(y + (0.9f - y) * 0.3f, r, 5);
    }

    [Theory]
    [InlineData(0.80f, 0.10f, 0.02f, 3.0f)]
    [InlineData(0.05f, 0.60f, 0.10f, 2.0f)]
    [InlineData(0.10f, 0.20f, 0.90f, 3.0f)]
    public void A_strong_boost_of_a_saturated_colour_keeps_its_hue_and_brightness_and_stays_in_gamut(float r, float g, float b, float factor)
    {
        float hue = Hue(r, g, b), y = ToneCurve.Luminance(r, g, b);
        SaturationMath.Apply(ref r, ref g, ref b, factor);
        Assert.True(MathF.Min(r, MathF.Min(g, b)) >= 0f);
        Assert.InRange(Math.Abs(Hue(r, g, b) - hue), 0f, 0.5f);
        Assert.InRange(ToneCurve.Luminance(r, g, b), y - 1e-4f, y + 1e-4f);
    }

    [Fact]
    public void The_response_rises_smoothly_with_the_slider_up_to_the_gamut_edge()
    {
        float previous = -1f;
        for (float factor = 1f; factor <= 4f; factor += 0.05f)
        {
            float r = 0.7f, g = 0.2f, b = 0.05f;
            SaturationMath.Apply(ref r, ref g, ref b, factor);
            Assert.True(r >= previous - 1e-6f, "monotonic");
            Assert.True(b >= 0f);
            previous = r;
        }
        // The factor itself is continuous at the knee.
        float y = 0.3f, min = 0.05f, max = y / (y - min), knee = SaturationMath.Knee * max;
        Assert.Equal(SaturationMath.LimitFactor(knee - 1e-3f, y, min), SaturationMath.LimitFactor(knee + 1e-3f, y, min), 2);
    }

    [Fact]
    public void A_colour_on_the_gamut_edge_is_not_changed_by_a_factor_of_one_and_gains_nothing_from_more()
    {
        float r = 0.8f, g = 0.3f, b = 0f;
        SaturationMath.Apply(ref r, ref g, ref b, 1f);
        Assert.Equal((0.8f, 0.3f, 0f), (r, g, b));
        SaturationMath.Apply(ref r, ref g, ref b, 2f);
        Assert.Equal(0.8f, r, 4);
        Assert.Equal(0.3f, g, 4);
        Assert.Equal(0f, b, 4);
    }
}
