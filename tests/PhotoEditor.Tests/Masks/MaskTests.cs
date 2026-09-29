using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Masks;

public class MaskTests
{
    private static readonly AdjustmentSettings Brighter = new() { Exposure = 1 };

    private static Mask MaskOf(params MaskComponent[] components) =>
        new() { Adjustments = Brighter, Components = [.. components] };

    [Fact]
    public void Add_IsUnion()
    {
        var m = MaskRasterizer.Rasterize(MaskOf(new RectComponent(0, 0, 0.5f, 1), new RectComponent(0.5f, 0, 1, 1)), 4, 1);
        Assert.Equal([1f, 1f, 1f, 1f], m);
    }

    [Fact]
    public void Subtract_RemovesCoverage()
    {
        var m = MaskRasterizer.Rasterize(MaskOf(
            new ValueComponent(1),
            new RectComponent(0, 0, 0.5f, 1) { Mode = MaskMode.Subtract }), 4, 1);
        Assert.Equal([0f, 0f, 1f, 1f], m);
    }

    [Fact]
    public void Intersect_Multiplies()
    {
        var m = MaskRasterizer.Rasterize(MaskOf(
            new RectComponent(0, 0, 0.75f, 1),
            new ValueComponent(0.5f) { Mode = MaskMode.Intersect }), 4, 1);
        Assert.Equal([0.5f, 0.5f, 0.5f, 0f], m);
    }

    [Fact]
    public void Invert_FlipsComponent()
    {
        var m = MaskRasterizer.Rasterize(MaskOf(new RectComponent(0, 0, 0.5f, 1) { Invert = true }), 4, 1);
        Assert.Equal([0f, 0f, 1f, 1f], m);
    }

    [Fact]
    public void FirstComponent_StartsTheMaskWhateverItsMode()
    {
        var m = MaskRasterizer.Rasterize(MaskOf(new ValueComponent(0.25f) { Mode = MaskMode.Intersect }), 2, 1);
        Assert.Equal([0.25f, 0.25f], m);
    }

    [Fact]
    public void EmptyMask_IsBlack()
    {
        Assert.All(MaskRasterizer.Rasterize(MaskOf(), 3, 3), v => Assert.Equal(0f, v));
        Assert.False(MaskOf().IsActive);
    }

    [Fact]
    public void MaskWithoutAdjustments_IsInactive() =>
        Assert.False((MaskOf(new ValueComponent(1)) with { Adjustments = AdjustmentSettings.Default }).IsActive);

    [Fact]
    public void Render_ChangesOnlyMaskedArea()
    {
        using var src = TestImages.Solid(new SKColor(90, 90, 90), 8);
        var state = new EditState { Masks = [MaskOf(new RectComponent(0, 0, 0.5f, 1))] };
        using var dst = CpuAdjustmentRenderer.Render(src, state);
        Assert.True(dst.GetPixel(1, 4).Red > 110);
        Assert.Equal(new SKColor(90, 90, 90), dst.GetPixel(6, 4));
    }

    [Fact]
    public void MaskAppliesOnTopOfGlobalAdjustments()
    {
        using var src = TestImages.Solid(new SKColor(90, 90, 90), 4);
        var global = new AdjustmentSettings { Exposure = 1 };
        var both = new EditState { Adjustments = global, Masks = [MaskOf(new ValueComponent(1))] };
        using var once = CpuAdjustmentRenderer.Render(src, global);
        using var twice = CpuAdjustmentRenderer.Render(src, both);
        using var direct = CpuAdjustmentRenderer.Render(src, new AdjustmentSettings { Exposure = 2 });
        Assert.True(twice.GetPixel(0, 0).Red > once.GetPixel(0, 0).Red);
        Assert.InRange(twice.GetPixel(0, 0).Red - direct.GetPixel(0, 0).Red, -1, 1);
    }

    public static TheoryData<string, EditState> ParityCases => new()
    {
        { "rect", new EditState { Masks = [MaskOf(new RectComponent(0.2f, 0.1f, 0.7f, 0.9f))] } },
        { "half + global", new EditState
            {
                Adjustments = new AdjustmentSettings { Contrast = 30, Saturation = -20 },
                Masks = [MaskOf(new ValueComponent(0.5f)) with { Adjustments = new AdjustmentSettings { Temperature = 60, Shadows = 50 } }],
            } },
        { "two masks", new EditState
            {
                Masks =
                [
                    MaskOf(new RectComponent(0, 0, 0.6f, 1)),
                    MaskOf(new RectComponent(0.4f, 0, 1, 1) { Invert = true }, new ValueComponent(0.3f) { Mode = MaskMode.Add })
                        with { Adjustments = new AdjustmentSettings { Exposure = -1, Blues = new HslBand(0, 50, 0) } },
                ],
            } },
        { "disabled", new EditState { Masks = [MaskOf(new ValueComponent(1)) with { Enabled = false }] } },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void ShaderMatchesCpu_WithMasks(string name, EditState state)
    {
        using var src = TestImages.Varied();
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        int diff = TestImages.MaxDifference(cpu, gpu, out var at);
        Assert.True(diff <= 2, $"{name}: max channel difference {diff} at {at}");
    }

    [Fact]
    public void EditState_EqualityComparesMaskContents()
    {
        var mask = MaskOf(new ValueComponent(1));
        var a = new EditState { Masks = [mask] };
        var b = new EditState { Masks = [mask with { }] };
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Masks = [mask with { Name = "Other" }] });
        Assert.True(EditState.Default.IsDefault);
    }
}

public class MaskImageCacheTests
{
    [Fact]
    public void SameComponents_ReuseImage_ChangedComponents_Rerasterise()
    {
        var cache = new MaskImageCache();
        var mask = new Mask { Components = [new ValueComponent(1)] };
        var a = cache.Get(mask, 8, 8);
        Assert.Same(a, cache.Get(mask with { Name = "renamed" }, 8, 8));
        Assert.NotSame(a, cache.Get(mask, 4, 4));
        var changed = mask with { Components = [new ValueComponent(0.5f)] };
        Assert.NotSame(cache.Get(mask, 4, 4), cache.Get(changed, 4, 4));
    }
}
