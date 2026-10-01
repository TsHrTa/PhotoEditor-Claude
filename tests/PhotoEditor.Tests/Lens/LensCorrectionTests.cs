using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Lens;
using SkiaSharp;

namespace PhotoEditor.Tests.Lens;

public sealed class LensCorrectionTests
{
    // Entries copied from the lensfun database (data/db/slr-canon.xml, mil-canon.xml; CC BY-SA 3.0), shortened.
    internal const string Canon = """
        <lensdatabase version="2">
            <mount><name>Canon RF</name><compat>Canon EF</compat></mount>
            <camera><maker>Canon</maker><model>Canon EOS 5D Mark II</model><mount>Canon EF</mount><cropfactor>1</cropfactor></camera>
            <camera><maker>Canon</maker><model>Canon EOS R5</model><mount>Canon RF</mount><cropfactor>1</cropfactor></camera>
            <camera><maker>Canon</maker><model>Canon EOS 90D</model><mount>Canon EF-S</mount><cropfactor>1.613</cropfactor></camera>
            <lens>
                <maker>Canon</maker>
                <model>Canon EF 24-105mm f/4L IS USM</model>
                <mount>Canon EF</mount>
                <cropfactor>1</cropfactor>
                <calibration>
                    <distortion model="ptlens" focal="24" a="0.017263" b="-0.049244" c="0"/>
                    <distortion model="ptlens" focal="28" a="0.010878" b="-0.024454" c="0"/>
                    <distortion model="ptlens" focal="105" a="0" b="0.009598" c="0"/>
                    <tca model="poly3" focal="24" br="-0.0000336" vr="1.0011673" bb="-0.0000857" vb="1.0001820"/>
                    <tca model="poly3" focal="105" br="0.0000381" vr="0.9991735" bb="-0.0001974" vb="0.9998595"/>
                    <vignetting model="pa" focal="24" aperture="4" distance="0.45" k1="-0.4795" k2="-0.5164" k3="0.1168"/>
                    <vignetting model="pa" focal="24" aperture="4" distance="1000" k1="-0.5460" k2="-0.2245" k3="-0.0825"/>
                    <vignetting model="pa" focal="24" aperture="8" distance="1000" k1="-0.7823" k2="0.8454" k3="-0.8131"/>
                </calibration>
            </lens>
            <lens>
                <maker>Canon</maker>
                <model>Canon EF 24-105mm f/4L IS II USM</model>
                <mount>Canon EF</mount>
                <cropfactor>1</cropfactor>
                <calibration><distortion model="ptlens" focal="24" a="0.02" b="-0.05" c="0"/></calibration>
            </lens>
            <lens>
                <maker>Canon</maker>
                <model>Canon RF 24-105mm F4L IS USM</model>
                <mount>Canon RF</mount>
                <cropfactor>1.0</cropfactor>
                <calibration><distortion model="ptlens" focal="24.0" a="0.024775" b="-0.084337" c="0.064191" /></calibration>
            </lens>
        </lensdatabase>
        """;

    [Fact]
    public void Matching_FindsCameraAndLens_AsCamerasNameThem()
    {
        var db = LensDatabase.Parse(Canon);
        var camera = db.FindCamera("Canon", "EOS 5D Mark II");
        Assert.Equal(1.0, camera?.CropFactor);
        Assert.NotNull(db.FindCamera("Canon", "Canon EOS R5"));
        Assert.Null(db.FindCamera("Canon", "EOS 5D Mark III"));

        Assert.Equal("Canon EF 24-105mm f/4L IS USM", db.FindLens("EF24-105mm f/4L IS USM", camera, 50)?.Name);
        Assert.Equal("Canon EF 24-105mm f/4L IS II USM", db.FindLens("EF24-105mm f/4L IS II USM", camera)?.Name);
        Assert.Equal("Canon RF 24-105mm F4L IS USM", db.FindLens("RF24-105mm F4 L IS USM", db.FindCamera("Canon", "EOS R5"))?.Name);
        Assert.Null(db.FindLens("50-50mm f/0-0", camera)); // LibRaw's name when the lens is unknown
        Assert.Null(db.FindLens("EF70-200mm f/2.8L IS USM", camera));
        Assert.Null(db.FindLens(null, camera));
    }

    [Fact]
    public void Profile_InterpolatesByFocalLengthAndAperture()
    {
        var lens = LensDatabase.Parse(Canon).Lenses[0];
        var at26 = LensProfile.For(lens, 26, 4);
        Assert.Equal(DistortionModel.PtLens, at26.DistortionModel);
        Assert.Equal((0.017263 + 0.010878) / 2, at26.Distortion[0], 6);
        Assert.Equal((-0.049244 - 0.024454) / 2, at26.Distortion[1], 6);
        // Vignetting: the farthest distance; f/5.6 is half-way (in stops) between f/4 and f/8.
        var at56 = LensProfile.For(lens, 24, 5.657);
        Assert.Equal((-0.5460 - 0.7823) / 2, at56.Vignetting![0], 3);
        Assert.Equal(-0.5460, LensProfile.For(lens, 24, null).Vignetting![0], 4); // unknown aperture: widest
        Assert.Equal(1.0011673, LensProfile.For(lens, 10, 4).TcaRed[0], 6); // below the range: clamped
    }

    /// <summary>
    /// A 600 × 400 grid photo (lines every 40 px) as the lens would record it: each recorded pixel shows what lies at
    /// the undistorted position (found by inverting the distortion).
    /// </summary>
    private static SKBitmap DistortedGrid(LensProfile profile, double mmPerPixel)
    {
        var bmp = new SKBitmap(new SKImageInfo(600, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 400; y++)
            for (int x = 0; x < 600; x++)
            {
                double dx = x + 0.5 - 300, dy = y + 0.5 - 200;
                double rd = Math.Sqrt(dx * dx + dy * dy) * mmPerPixel / profile.HuginUnitMm;
                double ru = rd;
                for (int i = 0; i < 30; i++)
                    ru -= (profile.Distort(ru) - rd) / ((profile.Distort(ru + 1e-6) - profile.Distort(ru)) / 1e-6);
                double k = rd > 1e-9 ? ru / rd : 1;
                double ux = 300 + dx * k, uy = 200 + dy * k;
                bool line = Math.Abs((ux + 20) % 40 - 20) < 1.5 || Math.Abs((uy + 20) % 40 - 20) < 1.5;
                bmp.SetPixel(x, y, line ? SKColors.Black : SKColors.White);
            }
        return bmp;
    }

    /// <summary>How far the dark line near y ≈ <paramref name="nominal"/> strays up and down across the photo.</summary>
    private static double LineBend(SKBitmap b, int nominal)
    {
        var ys = new List<double>();
        for (int x = 20; x < b.Width - 20; x += 10)
        {
            double sum = 0, weight = 0;
            for (int y = nominal - 15; y <= nominal + 15; y++)
            {
                double dark = 255 - b.GetPixel(x, y).Red;
                sum += dark * y;
                weight += dark;
            }
            if (weight > 0)
                ys.Add(sum / weight);
        }
        return ys.Max() - ys.Min();
    }

    [Fact]
    public void Distortion_StraightensLines_AndFillsTheFrame()
    {
        var lens = LensDatabase.Parse(Canon).Lenses[0];
        var profile = LensProfile.For(lens, 24, 4); // strong barrel distortion at 24 mm
        // A 600 × 400 photo from a full-frame sensor: 43.27 mm over 721 px.
        double mmPerPixel = LensProfile.FullFrameDiagonal / Math.Sqrt(600.0 * 600 + 400 * 400);
        using var photo = DistortedGrid(profile, mmPerPixel);
        var correction = new LensCorrection(600, 400, profile, cropFactor: 1);
        using var corrected = correction.Apply(photo);
        // The grid line 160 px above the centre bends in the recorded photo; corrected it is straight.
        double before = LineBend(photo, 42), after = LineBend(corrected, (int)Math.Round(200 - 160 * correction.Zoom));
        Assert.True(before > 4, $"recorded bend {before:F1} px");
        Assert.True(after < 1.2, $"corrected bend {after:F1} px (zoom {correction.Zoom:F3})");
        Assert.InRange(correction.Zoom, 1.0, 1.1);
    }

    [Fact]
    public void Tca_BringsRedAndBlueOntoGreen()
    {
        // A white disc whose red image is 2 % larger and blue 1.5 % smaller (lateral chromatic aberration).
        var bmp = new SKBitmap(new SKImageInfo(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        byte Disc(double x, double y, double scale) => (byte)(Math.Sqrt(x * x + y * y) / scale < 150 ? 230 : 20);
        for (int y = 0; y < 400; y++)
            for (int x = 0; x < 400; x++)
            {
                double dx = x + 0.5 - 200, dy = y + 0.5 - 200;
                bmp.SetPixel(x, y, new SKColor(Disc(dx, dy, 1.02), Disc(dx, dy, 1), Disc(dx, dy, 0.985)));
            }
        // Total colour difference along the middle row (the sharp edges leave some resampling blur either way).
        int Fringe(SKBitmap b) => Enumerable.Range(0, 400).Sum(x => Math.Abs(b.GetPixel(x, 200).Red - b.GetPixel(x, 200).Green)
            + Math.Abs(b.GetPixel(x, 200).Blue - b.GetPixel(x, 200).Green));
        var correction = new LensCorrection(400, 400, null, 1, redScale: 1.02, blueScale: 0.985);
        using var corrected = correction.Apply(bmp);
        Assert.True(Fringe(corrected) * 3 < Fringe(bmp), $"fringe {Fringe(bmp)} → {Fringe(corrected)}");
    }

    [Fact]
    public void Vignetting_TermsAreInTheCorrectedPhotosUnits()
    {
        var lens = LensDatabase.Parse(Canon).Lenses[0];
        var profile = LensProfile.For(lens, 24, 4) with { DistortionModel = null, TcaModel = null };
        // Full frame, 3:2: the photo's corner is the calibration's corner.
        var same = new LensCorrection(600, 400, profile, 1).VignettingTerms!;
        Assert.Equal(-0.5460, same[0], 3);
        // On an APS-C body (crop 1.6) the photo only reaches 1 / 1.6 of the way to the calibration's corner.
        var apsc = new LensCorrection(600, 400, profile, 1.6).VignettingTerms!;
        Assert.Equal(-0.5460 / (1.6 * 1.6), apsc[0], 3);
        Assert.Equal(1f / (1 - 0.5460f - 0.2245f - 0.0825f), LensVignetting.GainFromTerms(same, 1), 3);
        // Without distortion the table is the formula at each r².
        var table = new LensCorrection(600, 400, profile, 1).VignettingTable!;
        Assert.Equal(LensVignetting.GainFromTerms(same, 0.5f), LensVignetting.Gain(table, 0, 0.5f), 2);
    }

    [Fact]
    public void Warp_KeepsTheFineLayersPrecision_AndCarriesTheHeadroom()
    {
        // A smooth ramp stored as 8 bits + fine fractions; a small manual distortion resamples it.
        var photo = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        var above = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var fine = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Opaque));
        double Exact(double x) => 20 + x * 0.013; // 8-bit steps: a very shallow ramp
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 300; x++)
            {
                double v = Exact(x + 0.5);
                byte b = (byte)Math.Round(v);
                photo.SetPixel(x, y, new SKColor(b, b, b));
                byte f = (byte)Math.Round(Headroom.FineZero + (v - b) * 255);
                fine.SetPixel(x, y, new SKColor(f, f, f));
                above.SetPixel(x, y, new SKColor(0, 0, 0));
            }
        Headroom.Attach(photo, new Headroom(above, 0.3f, fine));
        var correction = new LensCorrection(300, 200, null, 1, manualDistortion: 0.3);
        using var corrected = correction.Apply(photo);
        var h = Headroom.Of(corrected)!;
        for (int x = 10; x < 290; x += 7)
        {
            var (_, g, _) = correction.Factors(x + 0.5 - 150, 0.5);
            double expected = Exact(150 + (x + 0.5 - 150) * g);
            double value = corrected.GetPixel(x, 100).Red + (h.Fine!.GetPixel(x, 100).Red - Headroom.FineZero) / 255.0;
            Assert.True(Math.Abs(value - expected) < 0.02, $"x {x}: {value:F3} vs {expected:F3}");
        }
    }

    [Fact]
    public void ResamplingKeepsFineDetail_ItIsNotBilinearSoft()
    {
        // A pattern with a 4-pixel period (one of the finest a sensor records), warped by a distortion that shifts
        // the samples by fractions of a pixel: bilinear sampling would keep about 84 % of its contrast on average,
        // the Catmull-Rom taps keep about 95 %.
        using var photo = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 300; x++)
            {
                byte v = (byte)Math.Round(128 + 100 * Math.Sin(2 * Math.PI * (x + 0.5) / 4));
                photo.SetPixel(x, y, new SKColor(v, v, v));
            }
        var correction = new LensCorrection(300, 200, null, 1, manualDistortion: 0.6);
        using var corrected = correction.Apply(photo);
        double sum = 0;
        int n = 0;
        for (int y = 90; y < 110; y++)
            for (int x = 60; x < 240; x++)
            {
                double d = corrected.GetPixel(x, y).Red - 128;
                sum += d * d;
                n++;
            }
        double rms = Math.Sqrt(sum / n), rmsIn = 100 / Math.Sqrt(2);
        Assert.True(rms / rmsIn > 0.9, $"contrast kept {rms / rmsIn:P0}");
    }}
