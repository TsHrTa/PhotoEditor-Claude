using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.Tests.Adjustments;

public class ToneCurveTests
{
    private static PreparedAdjustments P(AdjustmentSettings s) => PreparedAdjustments.From(s);

    private static float[] Samples => Enumerable.Range(0, 101).Select(i => i / 100f).ToArray();

    [Fact]
    public void Default_IsIdentity()
    {
        var p = P(AdjustmentSettings.Default);
        foreach (var x in Samples)
            Assert.Equal(x, ToneCurve.Apply(x, p), 5);
    }

    [Theory]
    [InlineData(100, 0, 0, 0, 0)]
    [InlineData(-100, 0, 0, 0, 0)]
    [InlineData(0, 100, 0, 0, 0)]
    [InlineData(0, -100, 0, 0, 0)]
    [InlineData(0, 0, 100, 0, 0)]
    [InlineData(0, 0, -100, 0, 0)]
    [InlineData(0, 0, 0, 100, -100)]
    [InlineData(0, 0, 0, -100, 100)]
    [InlineData(200, 0, 0, 0, 0)]
    [InlineData(-200, 0, 0, 0, 0)]
    [InlineData(0, 200, 0, 0, 0)]
    [InlineData(0, -200, 0, 0, 0)]
    [InlineData(0, 0, 200, 0, 0)]
    [InlineData(0, 0, -200, 0, 0)]
    [InlineData(0, 0, 0, 200, -200)]
    [InlineData(0, 0, 0, -200, 200)]
    [InlineData(-200, -200, 200, -200, 200)]
    [InlineData(200, 200, -200, 200, -200)]
    public void Curve_IsMonotonic(double contrast, double highlights, double shadows, double whites, double blacks)
    {
        var p = P(new AdjustmentSettings { Contrast = contrast, Highlights = highlights, Shadows = shadows, Whites = whites, Blacks = blacks });
        float prev = -1;
        foreach (var x in Samples)
        {
            float y = ToneCurve.Apply(x, p);
            Assert.True(y >= prev - 1e-5f, $"not monotonic at {x}: {y} < {prev}");
            prev = y;
        }
    }

    [Fact]
    public void BeyondHundred_GoesFurther()
    {
        float x = 0.2f;
        float at100 = ToneCurve.Apply(x, P(new AdjustmentSettings { Shadows = 100 }));
        float at200 = ToneCurve.Apply(x, P(new AdjustmentSettings { Shadows = 200 }));
        Assert.True(at200 > at100 + 0.02f, $"{at100} → {at200}");
        float dark100 = ToneCurve.Apply(x, P(new AdjustmentSettings { Shadows = -100 }));
        float dark200 = ToneCurve.Apply(x, P(new AdjustmentSettings { Shadows = -200 }));
        Assert.True(dark200 < dark100 - 0.01f, $"{dark100} → {dark200}");
    }

    [Fact]
    public void Contrast_KeepsPivotAndEnds()
    {
        var p = P(new AdjustmentSettings { Contrast = 70 });
        Assert.Equal(0f, ToneCurve.Apply(0, p), 5);
        Assert.Equal(0.5f, ToneCurve.Apply(0.5f, p), 5);
        Assert.Equal(1f, ToneCurve.Apply(1, p), 5);
        Assert.True(ToneCurve.Apply(0.25f, p) < 0.25f);
        Assert.True(ToneCurve.Apply(0.75f, p) > 0.75f);
    }

    [Fact]
    public void NegativeHighlights_RecoverOverexposedValues()
    {
        var p = P(new AdjustmentSettings { Highlights = -100 });
        Assert.True(ToneCurve.Apply(1.2f, p) < 1f);
        Assert.Equal(0.01f, ToneCurve.Apply(0.01f, p), 5); // the deepest shadows untouched
    }

    [Fact]
    public void Shadows_LiftDarkTonesButKeepBlackAndWhite()
    {
        var p = P(new AdjustmentSettings { Shadows = 100 });
        Assert.Equal(0f, ToneCurve.Apply(0, p), 5);
        Assert.Equal(1f, ToneCurve.Apply(1, p), 5);
        Assert.True(ToneCurve.Apply(0.3f, p) > 0.45f);
    }

    [Fact]
    public void Blacks_MoveBlackPoint_Whites_MoveWhitePoint()
    {
        Assert.True(ToneCurve.Apply(0, P(new AdjustmentSettings { Blacks = 100 })) > 0.1f);
        Assert.True(ToneCurve.Apply(1, P(new AdjustmentSettings { Whites = -100 })) < 0.9f);
        Assert.Equal(0.5f, ToneCurve.Apply(0.5f, P(new AdjustmentSettings { Whites = 100 })), 1);
    }

    /// <summary>
    /// Pulled down, Highlights brings values above white back (white itself by 1.09 stops at −100) and never changes
    /// the order of tones, even with the second pass at −200 over the whole headroom.
    /// </summary>
    [Fact]
    public void Highlights_RollsOffAboveWhite()
    {
        const float a = -PreparedAdjustments.MaxHighlightsShift;
        // One stop over white (linear 2 ≈ 1.37 perceptually) comes back under white at −100.
        Assert.True(ToneCurve.Highlights(1.37f, a) < 1f, $"{ToneCurve.Highlights(1.37f, a)}");
        Assert.Equal(-1.09f, MathF.Log2(MathF.Pow(ToneCurve.Highlights(1f, a), ToneCurve.PerceptualGamma)), 2);
        foreach (int passes in new[] { 1, 2 })
        {
            float prev = -1;
            for (float x = 0; x <= 2.6f; x += 0.01f)
            {
                float y = x;
                for (int i = 0; i < passes; i++) y = ToneCurve.Highlights(y, a);
                Assert.True(y > prev, $"not rising at {x} ({passes} passes)");
                prev = y;
            }
        }
    }

    /// <summary>
    /// Highlights −95 left a RAW's clouds as grey as the sky next to them (luma 191 / 183, Lightroom 214 / 185): the base
    /// curve's shoulder had pressed them together. On a RAW a pixel well above its darkened local mean near white gets
    /// its lift back; the sky's grain, pixels below their mean, midtones and JPEGs keep the plain darkening.
    /// </summary>
    [Fact]
    public void Highlights_GiveRawCloudsBackTheirBrightness()
    {
        var jpeg = P(new AdjustmentSettings { Highlights = -95 });
        var raw = jpeg.ForScene(true);
        float Gain(in PreparedAdjustments p, float mean, float pixel) => ToneCurve.LocalGain(pixel, p, mean / pixel, mean);
        // the R8 sample's cloud edge: pixel 0.985 over a local mean of 0.885
        Assert.InRange(Gain(raw, 0.885f, 0.985f) / Gain(jpeg, 0.885f, 0.985f), 1.15f, 1.6f);
        // sky grain (2 % above its mean), a pixel below its mean, a midtone above its mean: unchanged
        Assert.Equal(Gain(jpeg, 0.92f, 0.94f), Gain(raw, 0.92f, 0.94f), 5);
        Assert.Equal(Gain(jpeg, 0.885f, 0.8f), Gain(raw, 0.885f, 0.8f), 5);
        Assert.Equal(Gain(jpeg, 0.2f, 0.3f), Gain(raw, 0.2f, 0.3f), 5);
        // never brighter than the pixel was
        for (float pixel = 0.9f; pixel <= 1.6f; pixel += 0.05f)
            Assert.True(Gain(raw, 0.885f, pixel) <= 1f + 1e-5f, $"at {pixel}");
    }

    /// <summary>
    /// User report: Highlights made the whole photo greyish and changed colours, and did something else than
    /// Lightroom's. Measured on the user's Lightroom TIFFs (R8 sample, −95): Lightroom darkens by a number of stops
    /// that grows with the brightness, about 0.4 in the lower midtones, nothing in the deepest shadows. As a gain it
    /// keeps colour ratios (no grey veil); raising Highlights keeps its own curve (from 0.4, white stays white).
    /// </summary>
    [Fact]
    public void Highlights_DarkenLikeLightroom()
    {
        const float a = -PreparedAdjustments.MaxHighlightsShift;
        float Stops(float x) => MathF.Log2(MathF.Pow(ToneCurve.Highlights(x, a) / x, ToneCurve.PerceptualGamma));
        Assert.Equal(0f, Stops(0.015f), 3);
        Assert.InRange(Stops(0.25f), -0.46f, -0.42f);
        Assert.InRange(Stops(0.6f), -0.85f, -0.70f);
        // more darkening for brighter tones
        for (float x = 0.12f; x < 1f; x += 0.02f)
            Assert.True(Stops(x + 0.02f) <= Stops(x) + 1e-5f, $"at {x}");

        const float up = PreparedAdjustments.MaxHighlightsShift;
        Assert.Equal(0.3f, ToneCurve.Highlights(0.3f, up), 5);
        Assert.Equal(0.4f, ToneCurve.Highlights(0.4f, up), 5);
        for (float x = 0.4f; x < 1f; x += 0.01f)
        {
            float slope = (ToneCurve.Highlights(x + 0.005f, up) - ToneCurve.Highlights(x, up)) / 0.005f;
            Assert.InRange(slope, 0f, 1.7f);
        }
    }

    /// <summary>
    /// User report: raising Highlights changed colours. White stays white (only Whites moves it), the upper tones
    /// rise noticeably, and a brightened colour keeps its channel ratios instead of clipping one channel.
    /// </summary>
    [Fact]
    public void PositiveHighlights_BrightenWithoutClipping()
    {
        const float a = PreparedAdjustments.MaxHighlightsShift;
        Assert.Equal(1f, ToneCurve.Highlights(1f, a), 5);
        Assert.Equal(1.5f, ToneCurve.Highlights(1.5f, a), 5);
        Assert.True(ToneCurve.Highlights(0.7f, a) > 0.78f, $"{ToneCurve.Highlights(0.7f, a)}");
        // A saturated yellow whose red channel would go past white: limited, ratios kept, never darker.
        float gain = ToneCurve.LimitBrightening(0.9f, 1.6f);
        Assert.InRange(0.9f * gain, 0.9f, 1f);
        Assert.Equal(1.5f, ToneCurve.LimitBrightening(0.3f, 1.5f), 5);
        Assert.Equal(1f, ToneCurve.LimitBrightening(1.2f, 2f), 5);
    }

    /// <summary>User report: Exposure at max gave neon colours; colours above white now burn out towards white.</summary>
    [Fact]
    public void RollToWhite_FadesOverexposedColoursToWhite()
    {
        float r = 0.9f, g = 0.6f, b = 0.1f;
        ToneCurve.RollToWhite(ref r, ref g, ref b);
        Assert.Equal((0.9f, 0.6f, 0.1f), (r, g, b));
        r = 3f; g = 2f; b = 0.3f;
        ToneCurve.RollToWhite(ref r, ref g, ref b);
        Assert.Equal(3f, r);
        Assert.True(g > 2.6f && b > 1f, $"{g} {b}");
        // Barely over white: hardly any change.
        r = 1.02f; g = 0.5f; b = 0.2f;
        ToneCurve.RollToWhite(ref r, ref g, ref b);
        Assert.InRange(g, 0.5f, 0.52f);
    }
}
