using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Masks;
using PhotoEditor.Tests.Masks;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public sealed class HeadroomTests
{
    /// <summary>
    /// Un-brightened 16-bit RGB as LibRaw hands it over: 98 % of the pixels a dark-to-mid ramp (linear 0..0.5),
    /// 2 % bright (linear 0.5..1), so auto-brightening pushes the brightest 1 % above white.
    /// </summary>
    private static ushort[] RawRamp(int width, int height)
    {
        var rgb = new ushort[width * height * 3];
        int n = width * height, bright = n * 2 / 100;
        for (int i = 0; i < n; i++)
        {
            double linear = i < n - bright ? 0.5 * i / (n - bright) : 0.5 + 0.5 * (i - (n - bright)) / bright;
            var v = (ushort)Math.Round(RawImageLoader.ToCurve(linear) * 65535);
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = v;
        }
        return rgb;
    }

    [Fact]
    public void Develop_BrightensLikeLibRaw_AndKeepsWhatGoesAboveWhite()
    {
        const int w = 500, h = 200;
        var rgb = RawRamp(w, h);
        using var photo = RawImageLoader.Develop(rgb, w, h);
        var headroom = Headroom.Of(photo);
        Assert.NotNull(headroom);

        // The brightest 1 % become white or brighter: brightening ≈ 1 / 0.75.
        var pixels = photo.GetPixelSpan();
        var extra = headroom.Bitmap.GetPixelSpan();
        int above = 0;
        double gain = 0;
        for (int i = 0; i < w * h; i++)
        {
            double linear = RawImageLoader.FromCurve(rgb[i * 3] / 65535.0);
            if (extra[i * 4] > 0)
                above++;
            else if (gain == 0 && pixels[i * 4] == 255)
                gain = 1 / linear; // first pixel reaching white
        }
        Assert.InRange(above / (double)(w * h), 0.009, 0.0105);
        Assert.InRange(gain, 1.3, 1.36);

        // Below white: exactly the brightened curve; above: 255 plus the headroom, to its precision.
        for (int i = 0; i < w * h; i += 97)
        {
            double linear = RawImageLoader.FromCurve(rgb[i * 3] / 65535.0);
            double expected = RawImageLoader.ToCurve(gain * linear);
            double value = pixels[i * 4] / 255.0 + headroom.Extra(extra[i * 4]);
            Assert.True(Math.Abs(value - expected) <= Math.Max(1.0, headroom.Scale) / 255 + 0.005,
                $"pixel {i}: {value:F4} vs {expected:F4}");
        }
    }

    [Fact]
    public void Develop_FlatPhoto_HasNoHeadroom()
    {
        var rgb = Enumerable.Repeat((ushort)20000, 50 * 40 * 3).ToArray();
        using var photo = RawImageLoader.Develop(rgb, 50, 40);
        Assert.Null(Headroom.Of(photo));
        Assert.Equal(255, photo.GetPixel(10, 10).Red);
    }

    /// <summary>
    /// 160 × 120: the top half a blown sky (255 in the photo) whose real brightness, with cloud texture, is in the
    /// headroom (up to ~1 stop above white); the bottom half mid grey.
    /// </summary>
    private static SKBitmap BlownSky(bool withHeadroom = true)
    {
        var photo = new SKBitmap(new SKImageInfo(160, 120, SKColorType.Rgba8888, SKAlphaType.Premul));
        var extra = new SKBitmap(new SKImageInfo(160, 120, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 160; x++)
            {
                bool sky = y < 60;
                byte v = sky ? (byte)255 : (byte)(110 + 10 * Math.Sin(x * 0.7));
                photo.SetPixel(x, y, new SKColor(v, v, v));
                byte e = sky ? (byte)(120 + 100 * Math.Sin(x * 0.9) * Math.Cos(y * 0.8)) : (byte)0;
                extra.SetPixel(x, y, new SKColor(e, e, sky ? (byte)Math.Min(255, e + 30) : (byte)0));
            }
        if (withHeadroom)
            Headroom.Attach(photo, new Headroom(extra, 0.35f));
        else
            extra.Dispose();
        return photo;
    }

    private static (double Mean, double Std) SkyStats(SKBitmap b)
    {
        var v = new List<double>();
        for (int y = 2; y < 58; y++)
            for (int x = 2; x < 158; x++)
                v.Add(b.GetPixel(x, y).Green);
        double mean = v.Average();
        return (mean, Math.Sqrt(v.Average(d => (d - mean) * (d - mean))));
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, -100.0)]
    public void DarkeningABlownSky_BringsBackItsTexture(double exposure, double highlights)
    {
        var settings = new AdjustmentSettings { Exposure = exposure, Highlights = highlights };
        using var raw = BlownSky();
        using var jpeg = BlownSky(withHeadroom: false);
        using var recovered = CpuAdjustmentRenderer.Render(raw, settings);
        using var flat = CpuAdjustmentRenderer.Render(jpeg, settings);
        var (mean, std) = SkyStats(recovered);
        Assert.True(std > 8, $"texture std {std:F1}");
        Assert.True(mean < 250, $"mean {mean:F0}");
        Assert.True(SkyStats(flat).Std < 1);
        // Unedited, the photo looks as before: the headroom is clipped at the end.
        using var unedited = CpuAdjustmentRenderer.Render(raw, new AdjustmentSettings());
        int d = TestImages.MaxDifference(unedited, raw, out var at);
        Assert.True(d == 0, $"{d} at {at}");
    }

    private static Mask MaskOf(AdjustmentSettings adjustments, params MaskComponent[] components) =>
        new() { Adjustments = adjustments, Components = [.. components] };

    public static TheoryData<string, EditState> ParityCases => new()
    {
        { "exposure", new EditState { Adjustments = new AdjustmentSettings { Exposure = -1.2, Contrast = 20 } } },
        { "highlights", new EditState { Adjustments = new AdjustmentSettings { Highlights = -100, Shadows = 30 } } },
        { "filters", new EditState { Adjustments = new AdjustmentSettings { Exposure = -1, SharpenAmount = 80, NoiseLuminance = 40, Soften = 30 } } },
        { "in a mask", new EditState
            {
                Adjustments = new AdjustmentSettings { Exposure = 0.5 },
                Masks = [MaskOf(new AdjustmentSettings { Exposure = -1.5, Highlights = -50 }, new RectComponent(0.2f, 0, 0.8f, 0.7f))],
            } },
        { "two masks", new EditState
            {
                Masks =
                [
                    MaskOf(new AdjustmentSettings { Exposure = 1 }, new RectComponent(0, 0, 0.6f, 1)),
                    MaskOf(new AdjustmentSettings { Exposure = -2, Soften = 50 }, new RectComponent(0.3f, 0, 1, 0.8f)),
                ],
            } },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void ShaderMatchesCpu_WithHeadroom(string name, EditState state)
    {
        using var src = BlownSky();
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        int diff = TestImages.MaxDifference(cpu, gpu, out var at);
        Assert.True(diff <= 2, $"{name}: max channel difference {diff} at {at}");
    }

    [Fact]
    public void AMask_BringsBackWhatTheGlobalPassPushedAboveWhite()
    {
        // Any photo: +1 stop globally, −1 stop in a mask over everything = the photo again (no clipping between).
        using var src = TestImages.Varied(withAlpha: false);
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { Exposure = 1 },
            Masks = [MaskOf(new AdjustmentSettings { Exposure = -1 }, new ValueComponent(1))],
        };
        using var cpu = CpuAdjustmentRenderer.Render(src, state);
        using var gpu = AdjustmentShader.RenderRaster(src, state);
        Assert.True(TestImages.MaxDifference(cpu, src, out var at) <= 1, $"cpu at {at}");
        Assert.True(TestImages.MaxDifference(gpu, src, out at) <= 1, $"gpu at {at}");
    }

    [Fact]
    public void PreviewAndRestoredCopies_CarryTheHeadroom()
    {
        using var raw = BlownSky();
        var preview = PreviewImage.Create(raw.Copy() is var copy && Attach(copy, raw) ? copy : copy, maxPreviewSize: 80);
        Assert.Same(Headroom.Of(raw), Headroom.Of(preview.Full));
        var small = Headroom.Of(preview.Preview);
        Assert.NotNull(small);
        Assert.Equal((preview.Preview.Width, preview.Preview.Height), (small.Width, small.Height));
        Assert.Equal(Headroom.Of(raw)!.Scale, small.Scale);

        using var working = RestorePipeline.Blend(raw, null, 0, null, 0);
        Assert.Same(Headroom.Of(raw), Headroom.Of(working));
    }

    private static bool Attach(SKBitmap copy, SKBitmap from)
    {
        Headroom.Attach(copy, Headroom.Of(from));
        return true;
    }

    /// <summary>Real Canon CR2 when PHOTOEDITOR_RAW points to one: the photo matches LibRaw's own brightened output.</summary>
    [Fact]
    public void RealRaw_LooksAsLibRawsOutput_WithHeadroom()
    {
        var path = Environment.GetEnvironmentVariable("PHOTOEDITOR_RAW");
        if (path is null || !File.Exists(path))
            return;
        using var photo = RawImageLoader.Load(path);
        Assert.NotNull(Headroom.Of(photo));
        var settings = new ImageMagick.MagickReadSettings();
        settings.SetDefines(new ImageMagick.Formats.DngReadDefines { InterpolationQuality = ImageMagick.Formats.DngInterpolation.Ppg });
        using var libraw = new ImageMagick.MagickImage(path, settings);
        var reference = libraw.GetPixelsUnsafe().ToShortArray("RGB")!;
        var pixels = photo.GetPixelSpan();
        int max = 0;
        for (int i = 0, j = 0; i < reference.Length; i += 3, j += 4)
            for (int c = 0; c < 3; c++)
                max = Math.Max(max, Math.Abs(pixels[j + c] - (int)Math.Round(reference[i + c] / 257.0)));
        Assert.True(max <= 1, $"max difference {max}");
    }
}
