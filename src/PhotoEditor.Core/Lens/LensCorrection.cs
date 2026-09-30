using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Lens;

/// <summary>
/// The lens corrections of one photo, in its pixels: distortion (profile and / or manual) and lateral chromatic
/// aberration move pixels, so they are applied by resampling the photo (<see cref="Apply"/>); lens vignetting only
/// changes brightness, so the renderers apply it as a gain in linear light (<see cref="VignettingTable"/>), where
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
    /// The profile's vignetting terms in the photo's units: brightness is divided by 1 + k1 r² + k2 r⁴ + k3 r⁶ with
    /// r = 1 at the corner of the (uncorrected) photo; null without data.
    /// </summary>
    public float[]? VignettingTerms
    {
        get
        {
            if (Profile?.Vignetting is not { } k)
                return null;
            // lensfun's r = 1 is the calibration sensor's corner.
            double q = _halfDiagonal * _mmPerPixel / Profile.VignettingUnitMm;
            return [(float)(k[0] * q * q), (float)(k[1] * Math.Pow(q, 4)), (float)(k[2] * Math.Pow(q, 6))];
        }
    }

    /// <summary>Entries of <see cref="VignettingTable"/> (r² from 0 to 1).</summary>
    public const int VignettingTableSize = 256;

    /// <summary>
    /// The vignetting gain for the corrected photo by r² (r = 1 at its corner), as <see cref="VignettingTableSize"/>
    /// half-float-rounded entries: each output radius is traced back to where its pixels come from in the photo
    /// (distortion is radial), so steep vignetting near the corners is removed exactly. Null without data.
    /// </summary>
    public float[]? VignettingTable
    {
        get
        {
            if (VignettingTerms is not { } k)
                return null;
            var table = new float[VignettingTableSize];
            double angle = Math.Atan2(Height, Width); // along the diagonal
            for (int i = 0; i < VignettingTableSize; i++)
            {
                double r = Math.Sqrt((double)i / (VignettingTableSize - 1)) * _halfDiagonal;
                double green = Factors(r * Math.Cos(angle), r * Math.Sin(angle)).Green;
                double source = r * green / _halfDiagonal;
                table[i] = (float)(Half)LensVignetting.GainFromTerms(k, (float)(source * source));
            }
            return table;
        }
    }

    /// <summary>
    /// The corrected photo (RGBA8888, same size), with its <see cref="Headroom"/> layers corrected the same way;
    /// <paramref name="source"/> itself when nothing moves. The vignetting is left to the renderers
    /// (<see cref="VignettingTable"/>).
    /// </summary>
    public SKBitmap Apply(SKBitmap source)
    {
        if (source.Width != Width || source.Height != Height)
            throw new ArgumentException("The correction was made for another size.", nameof(source));
        if (IsGeometryIdentity)
            return source;
        using var converted = source.ColorType == SKColorType.Rgba8888 ? null : source.Copy(SKColorType.Rgba8888);
        var photo = converted ?? source;
        var headroom = Headroom.Of(source) is { } h && h.Width == Width && h.Height == Height ? h : null;
        var result = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, photo.AlphaType));
        var above = headroom is null ? null : new SKBitmap(headroom.Bitmap.Info);
        var fine = headroom?.Fine is null ? null : new SKBitmap(headroom.Fine.Info);
        Warp(photo, headroom?.Bitmap, headroom?.Fine, result, above, fine);
        if (headroom is not null)
            Headroom.Attach(result, new Headroom(above!, headroom.Scale, fine));
        return result;
    }

    /// <summary>Entries of the radial table (factors by squared distance from the centre).</summary>
    private const int RadialTableSize = 4096;

    /// <summary>
    /// Red, green and blue factors (see <see cref="Factors"/>) at squared distances 0 … half-diagonal² — the
    /// corrections are radial, so the warp interpolates this table instead of evaluating them per pixel.
    /// </summary>
    private float[] RadialTable()
    {
        var table = new float[RadialTableSize * 3];
        for (int i = 0; i < RadialTableSize; i++)
        {
            double d = Math.Sqrt((double)i / (RadialTableSize - 1)) * _halfDiagonal;
            var (r, g, b) = Factors(d, 0);
            table[i * 3] = (float)r;
            table[i * 3 + 1] = (float)g;
            table[i * 3 + 2] = (float)b;
        }
        return table;
    }

    private unsafe void Warp(SKBitmap photo, SKBitmap? above, SKBitmap? fine, SKBitmap outPhoto, SKBitmap? outAbove, SKBitmap? outFine)
    {
        var table = RadialTable();
        bool sameForAll = Profile?.TcaModel is null && RedScale == 1 && BlueScale == 1;
        var inP = new Plane(photo);
        var outP = new Plane(outPhoto);
        var inA = above is null ? default : new Plane(above);
        var outA = outAbove is null ? default : new Plane(outAbove);
        var inF = fine is null ? default : new Plane(fine);
        var outF = outFine is null ? default : new Plane(outFine);
        int w = Width, h = Height;
        float cx = w / 2f, cy = h / 2f;
        float toIndex = (RadialTableSize - 1) / (float)(_halfDiagonal * _halfDiagonal);
        Parallel.For(0, h, y =>
        {
            float dy = y + 0.5f - cy;
            byte* o = outP.At(0, y);
            byte* oa = outA.IsSet ? outA.At(0, y) : null;
            byte* of = outF.IsSet ? outF.At(0, y) : null;
            for (int x = 0; x < w; x++, o += 4)
            {
                float dx = x + 0.5f - cx;
                float t = MathF.Min((dx * dx + dy * dy) * toIndex, RadialTableSize - 1.001f);
                int i = (int)t;
                float frac = t - i;
                float kg = table[i * 3 + 1] + (table[i * 3 + 4] - table[i * 3 + 1]) * frac;
                var tapsG = new Taps(cx + dx * kg, cy + dy * kg, w, h);
                if (sameForAll)
                {
                    for (int ch = 0; ch < 3; ch++)
                        Channel(ch, tapsG, x);
                }
                else
                {
                    float kr = table[i * 3] + (table[i * 3 + 3] - table[i * 3]) * frac;
                    float kb = table[i * 3 + 2] + (table[i * 3 + 5] - table[i * 3 + 2]) * frac;
                    Channel(0, new Taps(cx + dx * kr, cy + dy * kr, w, h), x);
                    Channel(1, tapsG, x);
                    Channel(2, new Taps(cx + dx * kb, cy + dy * kb, w, h), x);
                }
                o[3] = (byte)(inP.Sample(tapsG, 3) + 0.5f);
                // premultiplied: a colour never exceeds its alpha
                if (o[0] > o[3]) o[0] = o[3];
                if (o[1] > o[3]) o[1] = o[3];
                if (o[2] > o[3]) o[2] = o[3];
                if (oa != null)
                    oa[x * 4 + 3] = 255;
                if (of != null)
                    of[x * 4 + 3] = 255;
            }

            void Channel(int ch, in Taps taps, int x)
            {
                float value = inP.Sample(taps, ch);
                byte* px = outP.At(x, y);
                if (inF.IsSet)
                {
                    // The photo and its fine layer together (their sum is the real value), then split again.
                    float exact = value + (inF.Sample(taps, ch) - Headroom.FineZero) / 255f; // in 8-bit steps
                    int rounded = Math.Clamp((int)MathF.Round(exact), 0, 255);
                    px[ch] = (byte)rounded;
                    outF.At(x, y)[ch] = (byte)Math.Clamp((int)MathF.Round(Headroom.FineZero + (exact - rounded) * 255f), 0, 255);
                }
                else
                    px[ch] = (byte)(value + 0.5f);
                if (inA.IsSet)
                    outA.At(x, y)[ch] = (byte)(inA.Sample(taps, ch) + 0.5f);
            }
        });
    }

    /// <summary>The 4 bilinear taps around a position (pixel centres at +0.5), clamped at the border.</summary>
    private readonly struct Taps
    {
        public readonly int X0, X1, Y0, Y1;
        public readonly float Fx, Fy;

        public Taps(float sx, float sy, int w, int h)
        {
            float u = sx - 0.5f, v = sy - 0.5f;
            int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
            Fx = u - x0;
            Fy = v - y0;
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

        public float Sample(in Taps t, int ch)
        {
            byte* r0 = _pixels + (long)t.Y0 * _rowBytes, r1 = _pixels + (long)t.Y1 * _rowBytes;
            float top = r0[t.X0 * 4 + ch] + (r0[t.X1 * 4 + ch] - r0[t.X0 * 4 + ch]) * t.Fx;
            float bottom = r1[t.X0 * 4 + ch] + (r1[t.X1 * 4 + ch] - r1[t.X0 * 4 + ch]) * t.Fx;
            return top + (bottom - top) * t.Fy;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// The lens vignetting gain table (<see cref="LensCorrection.VignettingTable"/>) attached to a photo image the
/// viewer shows, read by the shader; and the gain formula both renderers use.
/// </summary>
public static class LensVignetting
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, float[]> Attached = new();

    public static float[]? Of(object? photo) => photo is not null && Attached.TryGetValue(photo, out var k) ? k : null;

    public static void Attach(object photo, float[]? table)
    {
        if (table is not null)
            Attached.AddOrUpdate(photo, table);
    }

    /// <summary>
    /// The brightness factor at <paramref name="r2"/> = r² (r = 1 at the corner): the profile's vignetting removed
    /// (<paramref name="table"/>, interpolated linearly like a texture; null = none) and the manual vignetting
    /// (−1..1, ±1 stop at the corner) applied. Mirrored in the shader.
    /// </summary>
    public static float Gain(float[]? table, float manual, float r2)
    {
        float gain = 1f;
        if (table is not null)
        {
            float t = Math.Clamp(r2, 0f, 1f) * (table.Length - 1);
            int i = Math.Min((int)t, table.Length - 2);
            gain = table[i] + (table[i + 1] - table[i]) * (t - i);
        }
        return manual == 0f ? gain : gain * MathF.Pow(2f, manual * r2);
    }

    /// <summary>1 / (1 + k1 r² + k2 r⁴ + k3 r⁶) (limited to 20×).</summary>
    public static float GainFromTerms(float[] k, float r2) =>
        1f / MathF.Max(1f + k[0] * r2 + k[1] * r2 * r2 + k[2] * r2 * r2 * r2, 0.05f);
}
