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
    public void Develop_FlatPhoto_HasNothingAboveWhite()
    {
        var rgb = Enumerable.Repeat((ushort)20000, 50 * 40 * 3).ToArray();
        using var photo = RawImageLoader.Develop(rgb, 50, 40);
        var headroom = Headroom.Of(photo)!;
        Assert.All(headroom.Bitmap.Pixels, p => Assert.Equal(0, p.Red + p.Green + p.Blue));
        Assert.All(headroom.Fine!.Pixels, p => Assert.Equal(Headroom.FineZero, p.Red));
        Assert.Equal(255, photo.GetPixel(10, 10).Red);
    }

    /// <summary>
    /// 160 × 120: the top half a blown sky (255 in the photo) whose real brightness, with cloud texture, is in the
    /// headroom (up to ~1 stop above white); the bottom half mid grey.
    /// </summary>
    private static SKBitmap BlownSky(bool withHeadroom = true, bool baseCurve = false)
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
            Headroom.Attach(photo, new Headroom(extra, 0.35f) { BaseCurve = baseCurve });
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
        { "white balance", new EditState { Adjustments = new AdjustmentSettings { Temperature = 70, Tint = -40, Exposure = 0.4 } } },
        { "white balance in a mask", new EditState
            {
                Masks = [MaskOf(new AdjustmentSettings { Temperature = -80, Tint = 50 }, new RectComponent(0.2f, 0, 0.8f, 1))],
            } },
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
        foreach (bool baseCurve in new[] { false, true })
        {
            using var src = BlownSky(baseCurve: baseCurve);
            using var cpu = CpuAdjustmentRenderer.Render(src, state);
            using var gpu = AdjustmentShader.RenderRaster(src, state);
            int diff = TestImages.MaxDifference(cpu, gpu, out var at);
            Assert.True(diff <= 2, $"{name}, base curve {baseCurve}: max channel difference {diff} at {at}");
        }
    }

    [Fact]
    public void RawBaseCurve_InvertsExactly()
    {
        for (float x = 0; x <= 8; x += x < 0.05f ? 0.001f : 0.01f)
            Assert.Equal(x, RawBaseCurve.Invert(RawBaseCurve.Apply(x)), 3e-4f * Math.Max(1, x));
    }

    [Fact]
    public void RawExposure_BrightensAlongTheShoulder_AndKeepsHue()
    {
        // Midtones: about the same as a plain gain.
        float r = 0.1f, g = 0.1f, b = 0.1f;
        RawBaseCurve.ApplyExposure(ref r, ref g, ref b, 2);
        Assert.InRange(r, 0.17f, 0.2f);
        Assert.Equal(r, g);
        // A bright cloud (display 0.7): +1 stop stays near white instead of going 1 stop above it (1.4).
        float c = 0.7f, c2 = 0.7f, c3 = 0.7f;
        RawBaseCurve.ApplyExposure(ref c, ref c2, ref c3, 2);
        Assert.InRange(c, 0.85f, 1.1f);
        // −1 stop is the exact inverse of +1 stop.
        RawBaseCurve.ApplyExposure(ref c, ref c2, ref c3, 0.5f);
        Assert.Equal(0.7f, c, 4);
        // A colour keeps the order and relative place of its channels.
        float cr = 0.6f, cg = 0.4f, cb = 0.2f;
        RawBaseCurve.ApplyExposure(ref cr, ref cg, ref cb, 2);
        Assert.True(cr > cg && cg > cb);
        Assert.Equal(0.5f, (cg - cb) / (cr - cb), 4);
    }

    [Fact]
    public void ApplyScene_WithEqualGains_IsTheExposure()
    {
        float r = 0.6f, g = 0.4f, b = 0.2f, r2 = r, g2 = g, b2 = b;
        RawBaseCurve.ApplyScene(ref r, ref g, ref b, 1.7f, 1.7f, 1.7f);
        RawBaseCurve.ApplyExposure(ref r2, ref g2, ref b2, 1.7f);
        Assert.Equal((r2, g2, b2), (r, g, b));
    }

    [Fact]
    public void WhiteBalanceOnARawActsOnTheSceneValues_SoHighlightsRollOffInsteadOfTurningColoured()
    {
        // Warm white balance gains (what Temperature +60 gives): on display values a bright grey cloud is multiplied
        // outright (a strong orange cast, one channel far above white); on scene values the shoulder compresses it.
        var (gr, gg, gb) = PreparedAdjustments.WhiteBalanceGains(60, 0);
        float v = 0.9f;
        float r = v, g = v, b = v;
        RawBaseCurve.ApplyScene(ref r, ref g, ref b, gr, gg, gb);
        float naiveSpread = (v * gr - v * gb) / (v * gr + v * gb);
        float sceneSpread = (r - b) / (r + b);
        Assert.True(sceneSpread > 0 && sceneSpread < naiveSpread * 0.7f, $"scene {sceneSpread:0.000} vs display {naiveSpread:0.000}");
        // Midtones are balanced about the same either way (the curve is nearly a power law there).
        float mr = 0.1f, mg = 0.1f, mb = 0.1f;
        RawBaseCurve.ApplyScene(ref mr, ref mg, ref mb, gr, gg, gb);
        float midNaive = (0.1f * gr - 0.1f * gb) / (0.1f * gr + 0.1f * gb);
        Assert.InRange((mr - mb) / (mr + mb), midNaive * 0.6f, midNaive * 1.3f);
        // Neutral gains leave everything alone, and a grey stays grey under equal gains.
        float nr = 0.35f, ng = 0.35f, nb = 0.35f;
        RawBaseCurve.ApplyScene(ref nr, ref ng, ref nb, 1f, 1f, 1f);
        Assert.Equal(0.35f, nr, 5);
        Assert.Equal(nr, nb, 6);
    }
    [Fact]
    public void RawPhoto_ExposureDoesNotBlowTheSky()
    {
        // A bright textured sky just below white (top) over mid grey, once as a JPEG (plain gain) and once as a RAW
        // rendered with the base curve.
        SKBitmap Photo(bool raw)
        {
            var photo = new SKBitmap(new SKImageInfo(160, 120, SKColorType.Rgba8888, SKAlphaType.Premul));
            for (int y = 0; y < 120; y++)
                for (int x = 0; x < 160; x++)
                {
                    byte v = y < 60 ? (byte)(225 + 20 * Math.Sin(x * 0.9) * Math.Cos(y * 0.8)) : (byte)110;
                    photo.SetPixel(x, y, new SKColor(v, v, v));
                }
            if (raw)
                Headroom.Attach(photo, new Headroom(new SKBitmap(new SKImageInfo(160, 120, SKColorType.Rgba8888, SKAlphaType.Opaque)), 0.3f)
                    { BaseCurve = true });
            return photo;
        }
        using var plain = Photo(false);
        using var raw = Photo(true);
        var settings = new AdjustmentSettings { Exposure = 1 };
        using var a = CpuAdjustmentRenderer.Render(plain, settings);
        using var b = CpuAdjustmentRenderer.Render(raw, settings);
        // Grey (bottom): about the same brightening.
        Assert.InRange(b.GetPixel(80, 100).Red - a.GetPixel(80, 100).Red, -25, 10);
        // The sky: blown with a plain gain, still shaded below white with the base curve.
        Assert.True(SkyStats(a).Std < 1, $"plain {SkyStats(a).Std:0.0}");
        Assert.True(SkyStats(b).Std > 3 && SkyStats(b).Mean < 252, $"raw {SkyStats(b).Mean:0} ± {SkyStats(b).Std:0.0}");
        using var gpu = AdjustmentShader.RenderRaster(raw, new EditState { Adjustments = settings });
        Assert.True(TestImages.MaxDifference(b, gpu, out var at) <= 2, $"shader differs at {at}");
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

    /// <summary>
    /// A smooth, very dark RAW gradient (linear 0.0005 … 0.01 after brightening, 1024 steps) with a white top
    /// row so the brightening is 1; returns the photo and each column's exact encoded value.
    /// </summary>
    private static (SKBitmap Photo, double[] Exact) DarkRamp()
    {
        const int w = 1024, h = 8;
        var rgb = new ushort[w * h * 3];
        var exact = new double[w];
        for (int x = 0; x < w; x++)
        {
            double linear = 0.0005 + 0.0095 * x / (w - 1);
            var v = (ushort)Math.Round(RawImageLoader.ToCurve(linear) * 65535);
            exact[x] = RawImageLoader.ToCurve(RawImageLoader.FromCurve(v / 65535.0)); // what the 16 bits hold
            for (int y = 0; y < h; y++)
                rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = v;
        }
        for (int i = 0; i < w * 3; i++)
            rgb[i] = 65535; // a white top row (12 % of the pixels): the brightening stays 1
        return (RawImageLoader.Develop(rgb, w, h), exact);
    }

    [Fact]
    public void LiftedShadows_KeepTheirGradient_WithTheFineLayer()
    {
        var (photo, exact) = DarkRamp();
        using var _ = photo;
        var settings = new AdjustmentSettings { Exposure = 4 };
        using var fine = CpuAdjustmentRenderer.Render(photo, settings);
        using var gpu = AdjustmentShader.RenderRaster(photo, settings);
        using var coarse = photo.Copy();
        Headroom.Attach(coarse, Headroom.Of(photo)!.WithoutFine());
        using var eightBit = CpuAdjustmentRenderer.Render(coarse, settings);

        int Expected(int x) => (int)Math.Round(ColorMath.LinearToSrgb(Math.Min(16 * ColorMath.SrgbToLinear((float)exact[x]), 1f)) * 255);
        int ErrorOf(SKBitmap b) => Enumerable.Range(0, 1024).Max(x => Math.Abs(b.GetPixel(x, 4).Red - Expected(x)));
        // Largest jump between neighbouring columns: a smooth gradient moves ≤ 1–2 levels, banding jumps.
        int JumpOf(SKBitmap b) => Enumerable.Range(1, 1023).Max(x => Math.Abs(b.GetPixel(x, 4).Red - b.GetPixel(x - 1, 4).Red));
        Assert.True(ErrorOf(fine) <= 1, $"fine: error {ErrorOf(fine)}");
        Assert.True(ErrorOf(gpu) <= 1, $"shader: error {ErrorOf(gpu)}");
        Assert.True(JumpOf(fine) <= 2, $"fine: jump {JumpOf(fine)}");
        Assert.True(ErrorOf(eightBit) >= 4 && JumpOf(eightBit) >= 6, $"8-bit: error {ErrorOf(eightBit)}, jump {JumpOf(eightBit)}");
    }

    /// <summary>
    /// Real Canon CR2 when PHOTOEDITOR_RAW points to one: the base rendering is about as bright as the camera's own
    /// JPEG (in the lit parts within a stop) and keeps highlights above white.
    /// </summary>
    [Fact]
    public void RealRaw_IsAboutAsBrightAsTheCameraJpeg()
    {
        var path = Environment.GetEnvironmentVariable("PHOTOEDITOR_RAW");
        if (path is null || !File.Exists(path))
            return;
        using var photo = RawImageLoader.Load(path);
        Assert.NotNull(Headroom.Of(photo));
        using var jpeg = EmbeddedPreview.Load(path, 600)!;
        using var small = photo.Resize(new SKImageInfo(jpeg.Width, jpeg.Height), SKSamplingOptions.Default)!;
        double ratio = LitLuminance(small) / LitLuminance(jpeg);
        Assert.InRange(Math.Log2(ratio), -1.0, 1.0);
    }

    /// <summary>The 75th percentile of the linear luminance (the lit parts; cameras crush the shadows differently).</summary>
    private static double LitLuminance(SKBitmap b)
    {
        var l = new List<double>();
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2)
            {
                var c = b.GetPixel(x, y);
                l.Add(0.2126 * ColorMath.SrgbByteToLinear(c.Red) + 0.7152 * ColorMath.SrgbByteToLinear(c.Green)
                    + 0.0722 * ColorMath.SrgbByteToLinear(c.Blue));
            }
        l.Sort();
        return l[l.Count * 3 / 4];
    }

    [Fact]
    public void ReconstructHighlights_RaisesClippedChannels_FromTheOthers()
    {
        float[] clip = [60000, 30000, 40000];
        // Green clipped, red / blue not: green follows their average, so their texture carries over.
        float[] a = [44000, 30000, 26000];
        float[] b = [46000, 30000, 28000];
        RawImageLoader.ReconstructHighlights(a, clip);
        RawImageLoader.ReconstructHighlights(b, clip);
        Assert.Equal([44000f, 35000f, 26000f], a);
        Assert.Equal([46000f, 37000f, 28000f], b);
        // Well below every clip level: unchanged.
        float[] c = [45000, 20000, 30000];
        RawImageLoader.ReconstructHighlights(c, clip);
        Assert.Equal([45000f, 20000f, 30000f], c);
        // Every channel clipped: all at the brightest.
        float[] d = [60000, 30000, 40000];
        RawImageLoader.ReconstructHighlights(d, clip);
        Assert.Equal([60000f, 60000f, 60000f], d);
        // Nothing is lowered, even where the others are darker (a coloured highlight).
        float[] e = [10000, 30000, 5000];
        RawImageLoader.ReconstructHighlights(e, clip);
        Assert.Equal([10000f, 30000f, 5000f], e);
    }

    [Fact]
    public void BaseCurve_BrightensMidtones_AndRollsOffToWhiteAtTheSensorsWhite()
    {
        Assert.Equal(0, RawImageLoader.BaseCurve(0));
        Assert.Equal(1, RawImageLoader.BaseCurve(1), 5);
        Assert.InRange(RawImageLoader.BaseCurve(1 / 16f), 0.18f, 0.23f); // 4 stops down: about as bright as the camera's JPEG
        Assert.True(RawImageLoader.BaseCurve(2) > 1.2f);
        float prev = -1;
        for (float x = 0.01f; x <= 4; x += 0.01f)
        {
            float y = RawImageLoader.BaseCurve(x);
            Assert.True(y > prev);
            prev = y;
        }
        // The shoulder: less steep at 0.8 than in the midtones (per stop, i.e. in contrast).
        float Stop(float x) => RawImageLoader.BaseCurve(x * 1.1f) / RawImageLoader.BaseCurve(x);
        Assert.True(Stop(0.8f) < Stop(0.1f) - 0.02f, $"{Stop(0.8f)} vs {Stop(0.1f)}");
    }

    [Fact]
    public void DevelopSensor_WhiteIsWhite_ClippedGreenIsNotPink_AndBrighterGoesToHeadroom()
    {
        float[] clip = [60000, 30000, 40000];
        float[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        ushort[] rgb =
        [
            30000, 30000, 30000,  // a neutral that just clips (the lowest clip level)
            45000, 30000, 45000,  // brighter: green clipped, red / blue 1.5 × — a white cloud's core
            15000, 15000, 15000,  // one stop down: grey
        ];
        using var photo = RawImageLoader.DevelopSensor(rgb, 3, 1, clip, identity);
        var headroom = Headroom.Of(photo)!;
        var white = photo.GetPixel(0, 0);
        Assert.True(white.Red >= 254 && white.Green >= 254 && white.Blue >= 254, white.ToString());
        var core = photo.GetPixel(1, 0);
        Assert.Equal((255, 255, 255), (core.Red, core.Green, core.Blue));
        var extra = headroom.Bitmap.GetPixel(1, 0);
        Assert.True(extra.Red > 0 && extra.Red == extra.Green && extra.Green == extra.Blue, extra.ToString());
        var grey = photo.GetPixel(2, 0);
        Assert.Equal(grey.Red, grey.Green);
        Assert.InRange(grey.Red, 200, 240);
        Assert.Equal(0, headroom.Bitmap.GetPixel(2, 0).Red);
    }

    /// <summary>
    /// The user's Canon EOS R8 sky (samples/IMG_5645.CR3, when present) with its Lightroom settings: the clouds keep
    /// their shading instead of turning into a flat white glow, and their cores aren't tinted.
    /// </summary>
    [Fact]
    public void SampleSky_KeepsCloudTexture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PhotoEditor.slnx")))
            dir = Path.GetDirectoryName(dir);
        var path = dir is null ? null : Path.Combine(dir, "samples", "IMG_5645.CR3");
        if (path is null || !File.Exists(path))
            return;
        using var photo = RawImageLoader.Load(path);
        using var small = photo.Resize(new SKImageInfo(photo.Width / 8, photo.Height / 8), SKSamplingOptions.Default)!;
        Headroom.Attach(small, Headroom.Of(photo)!.Resized(small.Width, small.Height));
        var settings = new AdjustmentSettings { Exposure = 1.02, Contrast = 6, Highlights = -91, Shadows = 54, Whites = 25, Blacks = -33,
            Vibrance = 30, Saturation = 49 };
        using var result = CpuAdjustmentRenderer.Render(small, settings);
        // The top third of the portrait photo is sky and cloud.
        int blown = 0, pink = 0, total = 0;
        for (int y = 0; y < result.Height / 3; y++)
            for (int x = 0; x < result.Width; x++)
            {
                var c = result.GetPixel(x, y);
                total++;
                if (c.Red >= 254 && c.Green >= 254 && c.Blue >= 254)
                    blown++;
                // Tinted cores, not the purple fringes along branches (already purple before the edit).
                var s = small.GetPixel(x, y);
                if (c.Red > 200 && c.Blue > 200 && c.Green < Math.Min(c.Red, c.Blue) - 25 && s.Green >= Math.Min(s.Red, s.Blue) - 10)
                    pink++;
            }
        Assert.True(blown < total / 20, $"{100.0 * blown / total:0.0} % blown");
        Assert.True(pink < total / 200, $"{100.0 * pink / total:0.00} % pink");
    }
}
