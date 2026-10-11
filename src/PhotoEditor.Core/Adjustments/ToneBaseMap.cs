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

    /// <summary>Box radius (map pixels, applied twice) of the local mean Highlights acts on.</summary>
    private const int DetailRadius = 2;

    /// <summary>Largest pixel / local mean difference (stops) credited as detail.</summary>
    public const float MaxDetailStops = 1f;

    /// <summary>
    /// The local mean is range-limited (a bilateral grid): log luminance levels <see cref="GridStep"/> stops apart from
    /// <see cref="GridMin"/>, <see cref="GridLevels"/> of them; each pixel is averaged with its neighbours of similar
    /// brightness only, so a white sky next to dark ground keeps its own mean (a plain mean mixed in the ground and gave
    /// darkened highlights a bright halo along the horizon), while texture within about two stops (leaves and the
    /// gaps between them) is averaged. 2 stops kept 0.89 to 0.94 of the foliage detail at −95 (1 stop: 0.80, like no
    /// grid, the mean then follows the texture), and the horizon halo test still passes.
    /// </summary>
    public const float GridMin = -14f, GridStep = 2f;

    public const int GridLevels = 10;

    private ToneBaseMap(Half[] a, Half[] b, Half[] gridMean, Half[] gridWeight, int width, int height)
    {
        A = a;
        B = b;
        GridMean = gridMean;
        GridWeight = gridWeight;
        Width = width;
        Height = height;
    }

    /// <summary>Coefficient a per map pixel (0..1).</summary>
    public Half[] A { get; }

    /// <summary>Coefficient b per map pixel, stored offset: (b + 16) / 16.</summary>
    public Half[] B { get; }

    /// <summary>
    /// Range-limited local mean of the log luminance per grid level and map pixel (level k at [k · Width · Height]),
    /// stored offset like b. The guided base follows strong texture (foliage against the sky: a ≈ 1), so it cannot tell
    /// a leaf from its surroundings; Highlights acts on this mean instead.
    /// </summary>
    public Half[] GridMean { get; }

    /// <summary>Weight (share of the neighbourhood near each level, 0..1) per grid level and map pixel.</summary>
    public Half[] GridWeight { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The map as an RGBA half-float image for the shader (a in red, stored b in green).</summary>
    public SKImage Image => _image ??= CreateImage(Width * Height, i => (A[i], B[i]), Height);
    private SKImage? _image;

    /// <summary>The grid as an RGBA half-float image, the levels stacked vertically (stored mean in red, weight in green).</summary>
    public SKImage GridImage => _gridImage ??= CreateImage(Width * Height * GridLevels, i => (GridMean[i], GridWeight[i]), Height * GridLevels);
    private SKImage? _gridImage;

    private SKImage CreateImage(int count, Func<int, (Half, Half)> texel, int height)
    {
        var halves = new Half[count * 4];
        for (int i = 0; i < count; i++)
        {
            (halves[i * 4], halves[i * 4 + 1]) = texel(i);
            halves[i * 4 + 2] = (Half)0f;
            halves[i * 4 + 3] = (Half)1f;
        }
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
        var info = new SKImageInfo(Width, height, SKColorType.RgbaF16, SKAlphaType.Opaque);
        using var data = SKData.CreateCopy(bytes);
        return SKImage.FromPixels(info, data, Width * 8) ?? throw new InvalidOperationException("Could not create the tone map image.");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKImage, ToneBaseMap> Cache = new();

    /// <summary>The map of <paramref name="photo"/>, computed once per image object.</summary>
    public static ToneBaseMap For(SKImage photo) => Cache.GetValue(photo, Compute);

    /// <summary>
    /// Lets <paramref name="to"/> (e.g. the photo with a few spots removed) use the map already computed for
    /// <paramref name="from"/>, instead of computing its own.
    /// </summary>
    public static void Share(SKImage from, SKImage to)
    {
        if (Cache.TryGetValue(from, out var map))
            Cache.AddOrUpdate(to, map);
    }

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
        var (gridMean, gridWeight) = ComputeGrid(l, w, h);
        return new ToneBaseMap(ha, hb, gridMean, gridWeight, w, h);
    }

    /// <summary>
    /// The bilateral grid: each map pixel's log luminance is split between the two nearest levels (linear weights),
    /// the sums and weights are blurred spatially per level, the mean per level is their ratio.
    /// </summary>
    private static (Half[] Mean, Half[] Weight) ComputeGrid(float[] l, int w, int h)
    {
        int n = w * h;
        var mean = new Half[n * GridLevels];
        var weight = new Half[n * GridLevels];
        Parallel.For(0, GridLevels, k =>
        {
            var sum = new float[n];
            var wt = new float[n];
            for (int i = 0; i < n; i++)
            {
                float d = MathF.Abs((l[i] - GridMin) / GridStep - k);
                if (d < 1f)
                {
                    wt[i] = 1f - d;
                    sum[i] = (1f - d) * l[i];
                }
            }
            sum = GuidedFilter.BoxMean(GuidedFilter.BoxMean(sum, w, h, DetailRadius), w, h, DetailRadius);
            wt = GuidedFilter.BoxMean(GuidedFilter.BoxMean(wt, w, h, DetailRadius), w, h, DetailRadius);
            float level = GridMin + k * GridStep;
            for (int i = 0; i < n; i++)
            {
                float m = wt[i] > 1e-4f ? sum[i] / wt[i] : level;
                mean[k * n + i] = (Half)((m + BOffset) / BOffset);
                weight[k * n + i] = (Half)wt[i];
            }
        });
        return (mean, weight);
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
    public float BaseRatio(int x, int y, int imageWidth, int imageHeight, float logLuminance) =>
        Ratios(x, y, imageWidth, imageHeight, logLuminance).Base;

    /// <summary>
    /// <see cref="BaseRatio"/> and the detail ratio: the range-limited local mean (the grid levels around the pixel's own
    /// brightness, weighted) over the pixel, the difference limited to ±<see cref="MaxDetailStops"/>. Mirrored in the shader.
    /// </summary>
    public (float Base, float Detail) Ratios(int x, int y, int imageWidth, int imageHeight, float logLuminance)
    {
        float u = (x + 0.5f) * Width / imageWidth - 0.5f;
        // vertically clamped to the level's band before sampling, as the shader does (its grid image stacks the levels)
        float v = Math.Clamp((y + 0.5f) * Height / imageHeight - 0.5f, 0f, Height - 1);
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float Bilinear(Half[] map, int offset = 0)
        {
            float At(int xi, int yi) => (float)map[offset + Math.Clamp(yi, 0, Height - 1) * Width + Math.Clamp(xi, 0, Width - 1)];
            float top = At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx;
            float bottom = At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx;
            return top * (1 - fy) + bottom * fy;
        }
        float a = Bilinear(A), b = Bilinear(B) * BOffset - BOffset;
        float t = Math.Clamp((logLuminance - GridMin) / GridStep, 0f, GridLevels - 1.001f);
        int k = (int)t;
        float f = t - k;
        int n = Width * Height;
        float w0 = Bilinear(GridWeight, k * n) * (1 - f), w1 = Bilinear(GridWeight, (k + 1) * n) * f;
        float m0 = Bilinear(GridMean, k * n) * BOffset - BOffset, m1 = Bilinear(GridMean, (k + 1) * n) * BOffset - BOffset;
        float mean = w0 + w1 > 1e-4f ? (w0 * m0 + w1 * m1) / (w0 + w1) : logLuminance;
        return (RatioFrom(a, b, logLuminance), DetailRatioFrom(mean, logLuminance));
    }

    /// <summary>2^clamp(m − L, ±<see cref="MaxDetailStops"/>). Mirrored in the shader.</summary>
    public static float DetailRatioFrom(float mean, float logLuminance) =>
        MathF.Pow(2f, Math.Clamp(mean - logLuminance, -MaxDetailStops, MaxDetailStops));

    /// <summary>2^(a·L + b − L): the base over the pixel's own luminance. Mirrored in the shader.</summary>
    public static float RatioFrom(float a, float b, float logLuminance) => MathF.Pow(2f, a * logLuminance + b - logLuminance);
}
