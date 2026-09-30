using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Retouch;
using SkiaSharp;

namespace PhotoEditor.Tests.Retouch;

public sealed class RetouchingTests
{
    /// <summary>
    /// 400 × 300 "skin": a left-to-right brightness gradient (90 → 190) with fine texture (±6), and dark
    /// blemishes (radius 8) at the given centres.
    /// </summary>
    private static SKBitmap Skin(params (int X, int Y)[] blemishes)
    {
        var bmp = new SKBitmap(new SKImageInfo(400, 300, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
            {
                double v = Base(x, y);
                foreach (var (bx, by) in blemishes)
                    if ((x - bx) * (x - bx) + (y - by) * (y - by) <= 64)
                        v -= 70;
                byte l = (byte)Math.Clamp(Math.Round(v), 0, 255);
                bmp.SetPixel(x, y, new SKColor(l, (byte)(l * 0.8), (byte)(l * 0.7)));
            }
        return bmp;
    }

    private static double Base(int x, int y) => 90 + x * 0.25 + 6 * Math.Sin(x * 1.3) * Math.Cos(y * 1.7);

    private static double MeanRed(SKBitmap b, int cx, int cy, int r)
    {
        double sum = 0;
        int n = 0;
        for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                {
                    sum += b.GetPixel(x, y).Red;
                    n++;
                }
        return sum / n;
    }

    private static BrushPoint N(double x, double y) => new((float)(x / 400), (float)(y / 300));

    [Fact]
    public void Heal_RemovesABlemish_AndMatchesTheSurroundings_EvenFromADarkerSource()
    {
        using var src = Skin((250, 150));
        // Source 80 px to the left: the gradient makes it 20 levels darker there.
        var spot = new Spot { Center = N(250, 150), Source = N(170, 150), Radius = 14f / 400, Feather = 0.3f };
        using var healed = Retouching.Apply(src, [spot]);
        double expected = Enumerable.Range(242, 17).Average(x => 90 + x * 0.25);
        Assert.InRange(MeanRed(healed, 250, 150, 8), expected - 3, expected + 3);
        // Clone copies the darker source as is.
        using var cloned = Retouching.Apply(src, [spot with { Mode = SpotMode.Clone, Feather = 0 }]);
        Assert.InRange(MeanRed(cloned, 250, 150, 8), expected - 23, expected - 17);
        // The texture comes from the source (not a blur): the healed patch keeps its spread.
        var reds = new List<double>();
        for (int x = 243; x <= 257; x++)
            reds.Add(healed.GetPixel(x, 150).Red - (90 + x * 0.25));
        double spread = Math.Sqrt(reds.Average(v => v * v));
        Assert.InRange(spread, 2, 8);
        // Outside the circle nothing changes.
        Assert.Equal(src.GetPixel(280, 150), healed.GetPixel(280, 150));
        Assert.Equal(src.GetPixel(250, 130), healed.GetPixel(250, 130));
    }

    [Fact]
    public void Clone_CopiesTheSource_AndOpacityZeroChangesNothing()
    {
        using var src = Skin((200, 150));
        var spot = new Spot { Mode = SpotMode.Clone, Center = N(200, 150), Source = N(300, 100), Radius = 10f / 400, Feather = 0 };
        using var cloned = Retouching.Apply(src, [spot]);
        Assert.Equal(src.GetPixel(300, 100), cloned.GetPixel(200, 150));
        Assert.Equal(src.GetPixel(303, 98), cloned.GetPixel(203, 148));
        using var none = Retouching.Apply(src, [spot with { Opacity = 0 }]);
        Assert.Equal(0, TestImages.MaxDifference(src, none, out _));
        Assert.Same(src, Retouching.Apply(src, []));
    }

    [Fact]
    public void FindSource_PicksAPlainNearbyAreaInsideThePhoto_NotAnotherBlemish()
    {
        // Blemishes all around the one being removed, except below-left.
        var others = new[] { (230, 150), (170, 150), (200, 120), (200, 180), (225, 125), (225, 175), (175, 125) };
        using var src = Skin([(200, 150), .. others]);
        var source = Retouching.FindSource(src, N(200, 150), 12f / 400);
        double sx = source.X * 400, sy = source.Y * 300;
        double distance = Math.Sqrt((sx - 200) * (sx - 200) + (sy - 150) * (sy - 150));
        Assert.InRange(distance, 24, 90);
        foreach (var (bx, by) in others)
            Assert.True(Math.Sqrt((sx - bx) * (sx - bx) + (sy - by) * (sy - by)) > 12 + 8, $"source {sx:F0},{sy:F0} on the blemish at {bx},{by}");
        // Healing from it removes the blemish.
        var spot = new Spot { Center = N(200, 150), Source = source, Radius = 12f / 400 };
        using var healed = Retouching.Apply(src, [spot]);
        Assert.InRange(MeanRed(healed, 200, 150, 7), 90 + 200 * 0.25 - 4, 90 + 200 * 0.25 + 4);
    }

    [Fact]
    public void Spots_AreResolutionIndependent()
    {
        using var src = Skin((250, 150), (120, 80));
        var spots = new[]
        {
            new Spot { Center = N(250, 150), Source = N(200, 190), Radius = 14f / 400 },
            new Spot { Center = N(120, 80), Source = N(160, 60), Radius = 12f / 400, Mode = SpotMode.Clone },
        };
        using var full = Retouching.Apply(src, spots);
        using var small = PreviewImage.Downscale(src, 200, 150);
        using var smallHealed = Retouching.Apply(small, spots);
        using var fullDown = PreviewImage.Downscale(full, 200, 150);
        Assert.InRange(MeanRed(smallHealed, 125, 75, 4) - MeanRed(fullDown, 125, 75, 4), -3, 3);
        Assert.InRange(MeanRed(smallHealed, 60, 40, 4) - MeanRed(fullDown, 60, 40, 4), -3, 3);
    }

    [Fact]
    public void Renderers_ApplySpotsFirst_AndMatch()
    {
        using var src = Skin((250, 150));
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { Exposure = 0.3, Clarity = 40, Texture = 30 },
            Spots = [new Spot { Center = N(250, 150), Source = N(180, 120), Radius = 14f / 400 }],
        };
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var retouched = Retouching.Apply(src, state.Spots);
        using var expected = CpuAdjustmentRenderer.Render(retouched, state with { Spots = [] });
        Assert.Equal(0, TestImages.MaxDifference(cpu, expected, out _));
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
    }

    [Fact]
    public void HeadroomIsRetouchedToo()
    {
        using var src = Skin();
        var extra = new SKBitmap(new SKImageInfo(400, 300, SKColorType.Rgba8888, SKAlphaType.Opaque));
        extra.Erase(new SKColor(0, 0, 0));
        using (var canvas = new SKCanvas(extra))
            canvas.DrawCircle(300, 100, 20, new SKPaint { Color = new SKColor(200, 200, 200) });
        Headroom.Attach(src, new Headroom(extra, 0.3f));
        var spot = new Spot { Mode = SpotMode.Clone, Center = N(100, 200), Source = N(300, 100), Radius = 10f / 400, Feather = 0 };
        using var result = Retouching.Apply(src, [spot]);
        var headroom = Headroom.Of(result);
        Assert.NotNull(headroom);
        Assert.Equal(200, headroom.Bitmap.GetPixel(100, 200).Red);
        Assert.Equal(0, Headroom.Of(src)!.Bitmap.GetPixel(100, 200).Red); // the original is untouched
    }

    /// <summary>The skin scene with a dark diagonal "wire" (3 px wide) from (60, 60) to (340, 240).</summary>
    private static SKBitmap Wire()
    {
        var bmp = Skin();
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
            {
                // distance to the line through (60, 60) and (340, 240)
                double t = Math.Clamp(((x - 60) * 280 + (y - 60) * 180) / (280.0 * 280 + 180 * 180), 0, 1);
                double dx = 60 + 280 * t - x, dy = 60 + 180 * t - y;
                if (dx * dx + dy * dy <= 2.25)
                    bmp.SetPixel(x, y, new SKColor(20, 16, 14));
            }
        return bmp;
    }

    [Fact]
    public void StrokeHeal_RemovesAWire_WithAnAutomaticSource()
    {
        using var src = Wire();
        var path = Enumerable.Range(0, 15).Select(i => N(60 + 280 * i / 14.0, 60 + 180 * i / 14.0)).ToList();
        var center = N(200, 150);
        float radius = 6f / 400;
        var source = Retouching.FindStrokeSource(src, path, radius, center);
        var spot = new Spot { Mode = SpotMode.Heal, Path = [.. path], Center = center, Source = source, Radius = radius, Feather = 0.3f };
        using var healed = Retouching.Apply(src, [spot]);
        // Along the wire: no dark pixels left, brightness like the surroundings (the gradient).
        for (int i = 1; i < 14; i++)
        {
            int x = 60 + 280 * i / 14, y = 60 + 180 * i / 14;
            double expected = 90 + x * 0.25;
            Assert.InRange(healed.GetPixel(x, y).Red, expected - 12, expected + 12);
        }
        // The offset is beside the wire, not along it.
        double ox = (source.X - center.X) * 400, oy = (source.Y - center.Y) * 300;
        double along = Math.Abs(ox * 280 + oy * 180) / Math.Sqrt(280.0 * 280 + 180 * 180);
        double across = Math.Abs(ox * 180 - oy * 280) / Math.Sqrt(280.0 * 280 + 180 * 180);
        Assert.True(across > along && across >= 2 * 6, $"offset {ox:F0},{oy:F0}"); // clear of the stroke
        // Clone copies the shape from the source.
        using var cloned = Retouching.Apply(src, [spot with { Mode = SpotMode.Clone, Feather = 0 }]);
        Assert.Equal(src.GetPixel(200 + (int)Math.Round(ox), 150 + (int)Math.Round(oy)), cloned.GetPixel(200, 150));
        Assert.Equal(src.GetPixel(200, 60), healed.GetPixel(200, 60)); // away from the stroke: unchanged
    }

    [Fact]
    public void CopyPaste_CopiesSpotsOnlyWhenChosen_WithNewIds()
    {
        var spot = new Spot { Center = N(10, 20), Source = N(50, 60) };
        var source = new EditState { Spots = [spot], Adjustments = new AdjustmentSettings { Exposure = 1 } };
        var target = new EditState();
        Assert.Empty(SettingsTransfer.Apply(target, source, SettingsGroups.Default, 400, 300).Spots);
        var pasted = SettingsTransfer.Apply(target, source, SettingsGroups.Spots, 400, 300).Spots;
        Assert.Single(pasted);
        Assert.NotEqual(spot.Id, pasted[0].Id);
        Assert.Equal(spot with { Id = pasted[0].Id }, pasted[0]);
    }

    [Fact]
    public void Sidecar_RoundTripsSpots_AndClampsBadValues()
    {
        var spot = new Spot { Mode = SpotMode.Clone, Center = N(10, 20), Source = N(50, 60), Radius = 0.03f, Feather = 0.2f, Opacity = 0.7f };
        var state = new EditState { Spots = [spot] };
        var json = SidecarFile.Serialize(EditDocument.From(state));
        Assert.Equal(state, SidecarFile.Deserialize(json).ToState());
        var bad = json.Replace("\"radius\": 0.03", "\"radius\": 5").Replace("\"opacity\": 0.7", "\"opacity\": -1");
        var read = SidecarFile.Deserialize(bad).ToState().Spots[0];
        Assert.Equal(Spot.MaxRadius, read.Radius);
        Assert.Equal(0f, read.Opacity);
        Assert.False(state.IsDefault);
    }
}
