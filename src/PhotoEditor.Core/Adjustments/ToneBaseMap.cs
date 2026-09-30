using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// The smooth "base" brightness of a photo for local Highlights / Shadows (as Lightroom does since PV2012):
/// an edge-preserving average of log2 luminance, so a slider changes the brightness of areas while each pixel's
/// local detail (pixel / base) is kept — clouds keep their texture when highlights are pulled down.
/// <para>
/// Fast guided filter: a self-guided filter on log luminance computed on a ~512 px copy gives coefficient maps
/// (a, b); at full resolution base(x) = a(x) · L(x) + b(x), with a, b sampled bilinearly and L the pixel's own
/// log luminance, so the base follows edges at full resolution (no halos) while the map stays small.
/// </para>
/// Computed once per photo from the original; the shader and the CPU renderer sample the same half-float map.
/// </summary>
public sealed class ToneBaseMap
{
    public const int LongSide = 512;

    /// <summary>Box radius at map resolution (≈ 3 % of the long side): the size of the "areas".</summary>
    private const int Radius = 16;

    /// <summary>Regularisation in (log2 units)²: edges stronger than about √ε stops are kept.</summary>
    private const float Epsilon = 0.2f;

    /// <summary>b is stored as (b + <see cref="BOffset"/>) / <see cref="BOffset"/> so all stored values are 0..~1.</summary>
    public const float BOffset = 16f;

    /// <summary>Luminance floor for the log (≈ −13.3 stops).</summary>
    public const float MinLuminance = 1e-4f;

    private ToneBaseMap(Half[] a, Half[] b, int width, int height)
    {
        A = a;
        B = b;
        Width = width;
        Height = height;
    }

    /// <summary>Coefficient a per map pixel (0..1).</summary>
    public Half[] A { get; }

    /// <summary>Coefficient b per map pixel, stored offset: (b + 16) / 16.</summary>
    public Half[] B { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The map as an RGBA half-float image for the shader (a in red, stored b in green).</summary>
    public SKImage Image => _image ??= CreateImage();
    private SKImage? _image;

    private SKImage CreateImage()
    {
        var halves = new Half[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            halves[i * 4] = A[i];
            halves[i * 4 + 1] = B[i];
            halves[i * 4 + 2] = (Half)0f;
            halves[i * 4 + 3] = (Half)1f;
        }
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
        var info = new SKImageInfo(Width, Height, SKColorType.RgbaF16, SKAlphaType.Opaque);
        using var data = SKData.CreateCopy(bytes);
        return SKImage.FromPixels(info, data, Width * 8) ?? throw new InvalidOperationException("Could not create the tone map image.");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKImage, ToneBaseMap> Cache = new();

    /// <summary>The map of <paramref name="photo"/>, computed once per image object.</summary>
    public static ToneBaseMap For(SKImage photo) => Cache.GetValue(photo, Compute);

    public static ToneBaseMap Compute(SKImage photo)
    {
        using var bitmap = SKBitmap.FromImage(photo);
        return Compute(bitmap, Headroom.Of(photo));
    }

    public static ToneBaseMap Compute(SKBitmap photo) => Compute(photo, Headroom.Of(photo));

    /// <summary>The map of <paramref name="photo"/> including its highlights above white (<paramref name="headroom"/>, if any).</summary>
    public static ToneBaseMap Compute(SKBitmap photo, Headroom? headroom)
    {
        using var rgba = photo.ColorType == SKColorType.Rgba8888 && photo.AlphaType == SKAlphaType.Premul ? null : photo.Copy(SKColorType.Rgba8888);
        var source = rgba ?? photo;
        var (w, h) = PreviewImage.PreviewSize(source.Width, source.Height, LongSide);
        using var small = PreviewImage.Downscale(source, w, h);
        int n = w * h;
        var l = new float[n];
        var pixels = small.GetPixelSpan();
        if (headroom is not null && headroom.Width == source.Width && headroom.Height == source.Height)
        {
            using var extraSmall = PreviewImage.Downscale(headroom.Bitmap, w, h);
            var extra = extraSmall.GetPixelSpan();
            for (int i = 0; i < n; i++)
                l[i] = LogLuminance(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3],
                    headroom.Extra(extra[i * 4]), headroom.Extra(extra[i * 4 + 1]), headroom.Extra(extra[i * 4 + 2]));
        }
        else
        {
            for (int i = 0; i < n; i++)
                l[i] = LogLuminance(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]);
        }

        // Self-guided filter coefficients (He et al.), then their box means.
        var meanL = GuidedFilter.BoxMean(l, w, h, Radius);
        var squares = new float[n];
        for (int i = 0; i < n; i++)
            squares[i] = l[i] * l[i];
        var meanLL = GuidedFilter.BoxMean(squares, w, h, Radius);
        var a = new float[n];
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float variance = MathF.Max(meanLL[i] - meanL[i] * meanL[i], 0f);
            a[i] = variance / (variance + Epsilon);
            b[i] = meanL[i] - a[i] * meanL[i];
        }
        var meanA = GuidedFilter.BoxMean(a, w, h, Radius);
        var meanB = GuidedFilter.BoxMean(b, w, h, Radius);
        var ha = new Half[n];
        var hb = new Half[n];
        for (int i = 0; i < n; i++)
        {
            ha[i] = (Half)Math.Clamp(meanA[i], 0f, 1f);
            hb[i] = (Half)((meanB[i] + BOffset) / BOffset);
        }
        return new ToneBaseMap(ha, hb, w, h);
    }

    /// <summary>log2 of the linear luminance of a premultiplied RGBA8888 pixel (floored at <see cref="MinLuminance"/>).</summary>
    /// <remarks>The extra values are what the pixel's <see cref="Headroom"/> adds above white (encoded units).</remarks>
    public static float LogLuminance(byte r, byte g, byte b, byte alpha, float extraR = 0, float extraG = 0, float extraB = 0)
    {
        if (alpha == 0)
            return MathF.Log2(MinLuminance);
        float inv = 1f / alpha;
        float y = ToneCurve.Luminance(
            ColorMath.SrgbToLinear(MathF.Min(r * inv, 1f) + extraR),
            ColorMath.SrgbToLinear(MathF.Min(g * inv, 1f) + extraG),
            ColorMath.SrgbToLinear(MathF.Min(b * inv, 1f) + extraB));
        return MathF.Log2(MathF.Max(y, MinLuminance));
    }

    /// <summary>
    /// base / pixel luminance for image pixel (x, y) whose own log2 luminance is <paramref name="logLuminance"/>:
    /// multiplying the (edited) pixel luminance by this gives the base in the same state (exposure etc. included).
    /// The map is stretched over the image with bilinear, edge-clamped sampling at pixel centres (as Skia samples it).
    /// </summary>
    public float BaseRatio(int x, int y, int imageWidth, int imageHeight, float logLuminance)
    {
        float u = (x + 0.5f) * Width / imageWidth - 0.5f;
        float v = (y + 0.5f) * Height / imageHeight - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float Bilinear(Half[] map)
        {
            float At(int xi, int yi) => (float)map[Math.Clamp(yi, 0, Height - 1) * Width + Math.Clamp(xi, 0, Width - 1)];
            float top = At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx;
            float bottom = At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx;
            return top * (1 - fy) + bottom * fy;
        }
        float a = Bilinear(A), b = Bilinear(B) * BOffset - BOffset;
        return RatioFrom(a, b, logLuminance);
    }

    /// <summary>2^(a·L + b − L): the base over the pixel's own luminance. Mirrored in the shader.</summary>
    public static float RatioFrom(float a, float b, float logLuminance) => MathF.Pow(2f, a * logLuminance + b - logLuminance);
}
