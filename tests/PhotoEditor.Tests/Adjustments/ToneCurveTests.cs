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
}
