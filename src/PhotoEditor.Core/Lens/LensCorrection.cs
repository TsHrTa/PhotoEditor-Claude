using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Lens;

/// <summary>
/// The lens corrections of one photo, in its pixels: distortion (profile and / or manual) and lateral chromatic
/// aberration move pixels, so they are applied by resampling the photo (<see cref="Apply"/>); lens vignetting only
/// changes brightness, so the renderers apply it as a gain in linear light (<see cref="VignettingTerms"/>), where
/// brightened corners keep their precision.
/// <para>
/// The corrected photo is enlarged just enough (<see cref="Zoom"/>) that no empty border shows (like Lightroom's
/// "constrain crop"). Positions are measured from the photo's centre.
/// </para>
/// </summary>
public sealed class LensCorrection
{
    /// <summary>Manual distortion at ±100: r_d = r_u (1 − strength × amount × ρ²), ρ = 1 at the corner.</summary>
    public const double ManualDistortionStrength = 0.15;

    /// <param name="profile">Profile corrections, or null.</param>
    /// <param name="cropFactor">Crop factor of the photo's sensor (converts pixels to the profile's units).</param>
    /// <param name="manualDistortion">Manual distortion −1..1 (positive corrects barrel distortion).</param>
    /// <param name="redScale">Extra radial scale of the red image (automatic chromatic aberration, 1 = none).</param>
    /// <param name="blueScale">Same for blue.</param>
    public LensCorrection(int width, int height, LensProfile? profile, double cropFactor, double manualDistortion = 0,
        double redScale = 1, double blueScale = 1)
    {
        Width = width;
        Height = height;
        Profile = profile;
        ManualDistortion = manualDistortion;
        RedScale = redScale;
        BlueScale = blueScale;
        _halfDiagonal = Math.Sqrt((double)width * width + (double)height * height) / 2;
        _mmPerPixel = LensProfile.FullFrameDiagonal / Math.Max(cropFactor, 0.1) / (2 * _halfDiagonal);
        Zoom = IsGeometryIdentity ? 1 : FindZoom();
    }

    public int Width { get; }
    public int Height { get; }
    public LensProfile? Profile { get; }
    public double ManualDistortion { get; }
    public double RedScale { get; }
    public double BlueScale { get; }

    /// <summary>Enlargement so the corrected photo fills the frame (≥ 1).</summary>
    public double Zoom { get; }

    private readonly double _halfDiagonal;
    private readonly double _mmPerPixel;

    /// <summary>Nothing moves.</summary>
    public bool IsGeometryIdentity =>
        (Profile is null || (Profile.DistortionModel is null && Profile.TcaModel is null)) && ManualDistortion == 0
        && RedScale == 1 && BlueScale == 1;

    /// <summary>
    /// Where the output pixel at offset (<paramref name="dx"/>, <paramref name="dy"/>) from the centre takes its red,
    /// green and blue from, as factors of that offset (1 = same place).
    /// </summary>
    public (double Red, double Green, double Blue) Factors(double dx, double dy)
    {
        var (r, g, b) = Uncorrected(dx / Zoom, dy / Zoom);
        return (r / Zoom, g / Zoom, b / Zoom);
    }

    /// <summary>For an undistorted offset from the centre: the factors to the photo's red, green and blue positions.</summary>
    private (double Red, double Green, double Blue) Uncorrected(double ux, double uy)
    {
        double d = Math.Sqrt(ux * ux + uy * uy);
        if (d < 1e-9)
            return (1, 1, 1);
        double green = 1, red = 1, blue = 1;
        if (Profile is { } p)
        {
            double ru = d * _mmPerPixel / p.HuginUnitMm;
            double rd = p.Distort(ru);
            green = rd / ru;
            if (p.TcaModel is not null)
            {
                var (r, b) = p.Tca(rd);
                red = r / rd;
                blue = b / rd;
            }
        }
        if (ManualDistortion != 0)
        {
            double rho = d * green / _halfDiagonal;
            green *= 1 - ManualDistortionStrength * ManualDistortion * rho * rho;
        }
        return (green * red * RedScale, green, green * blue * BlueScale);
    }

    /// <summary>The smallest enlargement (≥ 1) for which every border pixel takes its colours from inside the photo.</summary>
    private double FindZoom()
    {
        bool Inside(double zoom)
        {
            const int samples = 48;
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
                foreach (var (x, y) in new[] { (t * Width, 0.0), (t * Width, (double)Height), (0.0, t * Height), ((double)Width, t * Height) })
                {
                    double ux = (x - Width / 2.0) / zoom, uy = (y - Height / 2.0) / zoom;
                    var f = Uncorrected(ux, uy);
                    foreach (double k in new[] { f.Red, f.Green, f.Blue })
                    {
                        double sx = Width / 2.0 + ux * k, sy = Height / 2.0 + uy * k;
                        if (sx < -0.01 || sy < -0.01 || sx > Width + 0.01 || sy > Height + 0.01)
                            return false;
                    }
                }
            }
            return true;
        }
        if (Inside(1))
            return 1;
        double lo = 1, hi = 1.05;
        while (!Inside(hi) && hi < 4)
            hi *= 1.2;
        for (int i = 0; i < 30; i++)
        {
            double mid = (lo + hi) / 2;
            if (Inside(mid))
                hi = mid;
            else
                lo = mid;
        }
        return hi;
    }

    /// <summary>
    /// Lens vignetting correction for the renderers: the photo's brightness is divided by
    /// 1 + k1 r² + k2 r⁴ + k3 r⁶ with r = 1 at the corner of the corrected photo (null = none).
    /// </summary>
    public float[]? VignettingTerms
    {
        get
        {
            if (Profile?.Vignetting is not { } k)
                return null;
            // lensfun's r = 1 is the calibration sensor's corner; the corrected photo's corner is
            // (half diagonal / zoom) of the original photo away from the centre.
            double q = _halfDiagonal / Zoom * _mmPerPixel / Profile.VignettingUnitMm;
            return [(float)(k[0] * q * q), (float)(k[1] * Math.Pow(q, 4)), (float)(k[2] * Math.Pow(q, 6))];
        }
    }

    /// <summary>
    /// The corrected photo (RGBA8888, same size), with its <see cref="Headroom"/> layers corrected the same way and
    /// its <see cref="LensVignetting"/> attached; <paramref name="source"/> itself when nothing moves (then only the
    /// vignetting is attached to it).
    /// </summary>
    public SKBitmap Apply(SKBitmap source)
    {
        if (source.Width != Width || source.Height != Height)
            throw new ArgumentException("The correction was made for another size.", nameof(source));
        if (IsGeometryIdentity)
        {
            LensVignetting.Attach(source, VignettingTerms);
            return source;
        }
        using var converted = source.ColorType == SKColorType.Rgba8888 ? null : source.Copy(SKColorType.Rgba8888);
        var photo = converted ?? source;
        var headroom = Headroom.Of(source) is { } h && h.Width == Width && h.Height == Height ? h : null;
        var result = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, photo.AlphaType));
        var above = headroom is null ? null : new SKBitmap(headroom.Bitmap.Info);
        var fine = headroom?.Fine is null ? null : new SKBitmap(headroom.Fine.Info);
        Warp(photo, headroom?.Bitmap, headroom?.Fine, result, above, fine);
        if (headroom is not null)
            Headroom.Attach(result, new Headroom(above!, headroom.Scale, fine));
        LensVignetting.Attach(result, VignettingTerms);
        return result;
    }

    private unsafe void Warp(SKBitmap photo, SKBitmap? above, SKBitmap? fine, SKBitmap outPhoto, SKBitmap? outAbove, SKBitmap? outFine)
    {
        var inP = new Plane(photo);
        var outP = new Plane(outPhoto);
        var inA = above is null ? default : new Plane(above);
        var outA = outAbove is null ? default : new Plane(outAbove);
        var inF = fine is null ? default : new Plane(fine);
        var outF = outFine is null ? default : new Plane(outFine);
        int w = Width, h = Height;
        double cx = w / 2.0, cy = h / 2.0;
        Parallel.For(0, h, y =>
        {
            double dy = y + 0.5 - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - cx;
                var f = Factors(dx, dy);
                for (int ch = 0; ch < 3; ch++)
                {
                    double k = ch == 0 ? f.Red : ch == 1 ? f.Green : f.Blue;
                    var t = new Taps(cx + dx * k, cy + dy * k, w, h);
                    float value = inP.Sample(t, ch);
                    byte* o = outP.At(x, y);
                    if (inF.IsSet)
                    {
                        // The photo and its fine layer together (their sum is the real value), then split again.
                        float exact = value + (inF.Sample(t, ch) - Headroom.FineZero) / 255f; // in 8-bit steps
                        byte rounded = (byte)Math.Clamp(MathF.Round(exact), 0, 255);
                        o[ch] = rounded;
                        outF.At(x, y)[ch] = (byte)Math.Clamp(MathF.Round(Headroom.FineZero + (exact - rounded) * 255f), 0, 255);
                    }
                    else
                        o[ch] = (byte)Math.Clamp(MathF.Round(value), 0, 255);
                    if (inA.IsSet)
                        outA.At(x, y)[ch] = (byte)Math.Clamp(MathF.Round(inA.Sample(t, ch)), 0, 255);
                }
                byte* px = outP.At(x, y);
                px[3] = (byte)Math.Clamp(MathF.Round(inP.Sample(new Taps(cx + dx * f.Green, cy + dy * f.Green, w, h), 3)), 0, 255);
                for (int ch = 0; ch < 3; ch++)
                    px[ch] = Math.Min(px[ch], px[3]); // premultiplied: a colour never exceeds its alpha
                if (outA.IsSet)
                    outA.At(x, y)[3] = 255;
                if (outF.IsSet)
                    outF.At(x, y)[3] = 255;
            }
        });
    }

    /// <summary>The 4 bilinear taps around a position (pixel centres at +0.5), clamped at the border.</summary>
    private readonly struct Taps
    {
        public readonly int X0, X1, Y0, Y1;
        public readonly float Fx, Fy;

        public Taps(double sx, double sy, int w, int h)
        {
            double u = sx - 0.5, v = sy - 0.5;
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            Fx = (float)(u - x0);
            Fy = (float)(v - y0);
            X0 = Math.Clamp(x0, 0, w - 1);
            X1 = Math.Clamp(x0 + 1, 0, w - 1);
            Y0 = Math.Clamp(y0, 0, h - 1);
            Y1 = Math.Clamp(y0 + 1, 0, h - 1);
        }
    }

    /// <summary>An RGBA8888 bitmap's pixels.</summary>
    private readonly unsafe struct Plane(SKBitmap bitmap)
    {
        private readonly byte* _pixels = (byte*)bitmap.GetPixels();
        private readonly int _rowBytes = bitmap.RowBytes;

        public bool IsSet => _pixels != null;

        public byte* At(int x, int y) => _pixels + (long)y * _rowBytes + x * 4;

        public float Sample(in Taps t, int ch) =>
            Lerp(Lerp(At(t.X0, t.Y0)[ch], At(t.X1, t.Y0)[ch], t.Fx), Lerp(At(t.X0, t.Y1)[ch], At(t.X1, t.Y1)[ch], t.Fx), t.Fy);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// Lens vignetting correction terms attached to a corrected photo (see <see cref="LensCorrection.VignettingTerms"/>),
/// read by the renderers.
/// </summary>
public static class LensVignetting
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, float[]> Attached = new();

    public static float[]? Of(object? photo) => photo is not null && Attached.TryGetValue(photo, out var k) ? k : null;

    public static void Attach(object photo, float[]? terms)
    {
        if (terms is not null)
            Attached.AddOrUpdate(photo, terms);
    }

    /// <summary>The brightness factor that removes the vignetting at <paramref name="r2"/> = r² (r = 1 at the corner).</summary>
    public static float Gain(float[] k, float r2) => 1f / MathF.Max(1f + k[0] * r2 + k[1] * r2 * r2 + k[2] * r2 * r2 * r2, 0.05f);
}
