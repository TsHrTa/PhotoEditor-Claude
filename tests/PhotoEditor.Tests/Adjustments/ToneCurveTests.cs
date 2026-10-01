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
        Assert.Equal(0.2f, ToneCurve.Apply(0.2f, p), 5); // shadows untouched
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

    [Fact]
    public void Highlights_RollsOffAboveWhite()
    {
        const float a = -PreparedAdjustments.MaxHighlightsShift;
        // Two stops over white (linear 4 ≈ 1.9 perceptually) comes back under white at −100 …
        Assert.True(ToneCurve.Highlights(1.9f, a) < 1f, $"{ToneCurve.Highlights(1.9f, a)}");
        Assert.True(ToneCurve.Highlights(3f, a) < 1f);
        // … keeping the gradient: brighter stays brighter.
        float prev = -1;
        for (float x = 0; x <= 4f; x += 0.05f)
        {
            float y = ToneCurve.Highlights(x, a);
            Assert.True(y > prev, $"not rising at {x}");
            prev = y;
        }
        // Continuous at white, the full amount there.
        Assert.Equal(ToneCurve.Highlights(1f, a), ToneCurve.Highlights(1.0001f, a), 3);
        Assert.Equal(1f + a, ToneCurve.Highlights(1f, a), 5);
    }

    /// <summary>
    /// User report: Highlights made the whole photo greyish. The midtones stay put and the bright range keeps
    /// at least ~45 % of its contrast at −100 (the old curve flattened it to ~30 %, starting below mid grey).
    /// </summary>
    [Fact]
    public void Highlights_LeaveMidtonesAndKeepContrast()
    {
        foreach (float a in new[] { -PreparedAdjustments.MaxHighlightsShift, PreparedAdjustments.MaxHighlightsShift })
        {
            Assert.Equal(0.3f, ToneCurve.Highlights(0.3f, a), 5);
            Assert.Equal(0.4f, ToneCurve.Highlights(0.4f, a), 5);
            Assert.InRange(ToneCurve.Highlights(0.5f, a) - 0.5f, -0.02f, 0.04f);
            for (float x = 0.4f; x < 1f; x += 0.01f)
            {
                float slope = (ToneCurve.Highlights(x + 0.005f, a) - ToneCurve.Highlights(x, a)) / 0.005f;
                Assert.InRange(slope, a < 0 ? 0.4f : 0f, 1.7f);
            }
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
