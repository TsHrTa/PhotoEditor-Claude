using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.Tests.Lens;

/// <summary>Uses the shared lens database, so these tests run one at a time.</summary>
[Collection(nameof(LensDatabaseCollection))]
public sealed class LensRenderTests : IDisposable
{
    private static readonly PhotoLens Shot = new("Canon", "EOS 5D Mark II", "EF24-105mm f/4L IS USM", 24, 4);

    public LensRenderTests() => LensSetup.SetDatabase(LensDatabase.Parse(LensCorrectionTests.Canon));

    public void Dispose() => LensSetup.SetDatabase(null);

    /// <summary>A flat mid-grey 600 × 400 photo darkened towards the corners as the lens does at 24 mm f/4.</summary>
    private static SKBitmap Vignetted()
    {
        var profile = LensProfile.For(LensSetup.Database.Lenses[0], 24, 4);
        var terms = new LensCorrection(600, 400, profile with { DistortionModel = null, TcaModel = null }, 1).VignettingTerms!;
        // (the render also removes the distortion; the gain table traces pixels back through it)
        var bmp = new SKBitmap(new SKImageInfo(600, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 400; y++)
            for (int x = 0; x < 600; x++)
            {
                double dx = x + 0.5 - 300, dy = y + 0.5 - 200;
                float r2 = (float)((dx * dx + dy * dy) / (300.0 * 300 + 200 * 200));
                float linear = ColorMath.SrgbToLinear(150 / 255f) / LensVignetting.GainFromTerms(terms, r2);
                byte v = (byte)Math.Round(ColorMath.LinearToSrgb(linear) * 255);
                bmp.SetPixel(x, y, new SKColor(v, v, v));
            }
        return bmp;
    }

    [Fact]
    public void ProfileVignetting_IsRemoved_OnCpuAndShader()
    {
        using var photo = Vignetted();
        Assert.True(photo.GetPixel(2, 2).Red < 110); // clearly darker corner
        var state = new EditState { Adjustments = new AdjustmentSettings { LensProfile = true } };
        using var cpu = CpuAdjustmentRenderer.Render(photo, state, Shot);
        using var gpu = AdjustmentShader.RenderRaster(photo, state, Shot);
        foreach (var (x, y) in new[] { (2, 2), (597, 397), (300, 5), (5, 200), (300, 200) })
            Assert.InRange(cpu.GetPixel(x, y).Red, 146, 154);
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
        // Without the profile nothing changes, and an unknown lens gets no profile.
        using var off = CpuAdjustmentRenderer.Render(photo, new EditState(), Shot);
        Assert.Equal(0, TestImages.MaxDifference(off, photo, out _));
        using var unknown = CpuAdjustmentRenderer.Render(photo, state, Shot with { LensName = "50-50mm f/0-0" });
        Assert.Equal(0, TestImages.MaxDifference(unknown, photo, out _));
    }

    [Fact]
    public void ProfileVignetting_WithATransform_IsStillRemoved()
    {
        // The gain must follow each output pixel back through the Transform to where it is in the lens's frame.
        using var photo = Vignetted();
        var state = new EditState
        {
            Adjustments = new AdjustmentSettings { LensProfile = true, TransformVertical = -45, TransformHorizontal = 20, TransformRotate = 3 },
        };
        using var cpu = CpuAdjustmentRenderer.Render(photo, state, Shot);
        using var gpu = AdjustmentShader.RenderRaster(photo, state, Shot);
        var bad = new List<string>();
        for (int y = 2; y < 400; y += 33)
            for (int x = 2; x < 600; x += 37)
                if (cpu.GetPixel(x, y).Red is < 145 or > 155)
                    bad.Add($"({x},{y})={cpu.GetPixel(x, y).Red}");
        Assert.True(bad.Count == 0, string.Join(" ", bad));
        Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"difference at {at}");
    }

    [Fact]
    public void ManualCorrections_ShaderMatchesCpu()
    {
        using var photo = TestImages.Varied(120, 80, withAlpha: false);
        foreach (var settings in new[]
        {
            new AdjustmentSettings { LensVignetting = 60, Exposure = -0.3 },
            new AdjustmentSettings { LensDistortion = 40, LensVignetting = -30 },
            new AdjustmentSettings { LensProfile = true, RemoveChromaticAberration = true, Contrast = 20 },
            new AdjustmentSettings { TransformVertical = 30, TransformAspect = -20, TransformScale = 80, LensVignetting = 50 },
        })
        {
            var state = new EditState { Adjustments = settings };
            using var cpu = CpuAdjustmentRenderer.Render(photo, state, Shot);
            using var gpu = AdjustmentShader.RenderRaster(photo, state, Shot);
            Assert.True(TestImages.MaxDifference(cpu, gpu, out var at) <= 2, $"{settings}: difference at {at}");
        }
        // Manual vignetting brightens the corners, not the centre.
        using var brighter = CpuAdjustmentRenderer.Render(TestImages.Solid(new SKColor(100, 100, 100), 50),
            new EditState { Adjustments = new AdjustmentSettings { LensVignetting = 100 } });
        Assert.True(brighter.GetPixel(0, 0).Red > 125);
        Assert.InRange(brighter.GetPixel(25, 25).Red, 99, 102);
    }

    [Fact]
    public void Match_DescribesWhatWasFound()
    {
        var db = LensSetup.Database;
        Assert.Equal("Canon EF 24-105mm f/4L IS USM", LensSetup.Match(Shot, db).Describe());
        Assert.Contains("No profile", LensSetup.Match(Shot with { LensName = "Sigma 35mm F1.4 DG HSM | A" }, db).Describe());
        Assert.Contains("doesn't name", LensSetup.Match(Shot with { LensName = null }, db).Describe());
        // Crop factor from the camera; unknown camera: from the 35 mm equivalent focal length.
        Assert.Equal(1.0, LensSetup.Match(Shot, db).CropFactor);
        Assert.Equal(1.6, LensSetup.Match(new PhotoLens("X", "Y", null, 10, null, 16), db).CropFactor, 3);
    }

    [Fact]
    public void Exif_GivesCameraLensFocalAndAperture()
    {
        var profile = new ImageMagick.ExifProfile();
        profile.SetValue(ImageMagick.ExifTag.Make, "Canon");
        profile.SetValue(ImageMagick.ExifTag.Model, "Canon EOS R5");
        profile.SetValue(ImageMagick.ExifTag.LensModel, "RF24-105mm F4 L IS USM");
        profile.SetValue(ImageMagick.ExifTag.FocalLength, new ImageMagick.Rational(35, 1));
        profile.SetValue(ImageMagick.ExifTag.FNumber, new ImageMagick.Rational(56, 10));
        var bytes = profile.ToByteArray()!;
        var tiff = bytes.AsSpan().StartsWith("Exif\0\0"u8) ? bytes[6..] : bytes;
        var lens = PhotoLens.FromExif(tiff);
        Assert.Equal(("Canon", "Canon EOS R5", "RF24-105mm F4 L IS USM"), (lens.CameraMaker, lens.CameraModel, lens.LensName));
        Assert.Equal(35, lens.FocalLength);
        Assert.Equal(5.6, lens.Aperture!.Value, 3);
    }
}

[CollectionDefinition(nameof(LensDatabaseCollection), DisableParallelization = true)]
public sealed class LensDatabaseCollection;
