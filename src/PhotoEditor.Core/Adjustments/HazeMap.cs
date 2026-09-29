using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// How hazy each part of a photo is, for Dehaze (dark channel prior, He et al. 2009): a small transmission map
/// t (1 = clear, low = hazy), stretched over the photo, plus the colour of the haze (atmospheric light, linear).
/// Computed once per photo from a ~256 px copy; the shader and the CPU renderer sample the same 8-bit map, so the
/// preview and the export agree.
/// </summary>
public sealed class HazeMap
{
    public const int LongSide = 256;

    /// <summary>Radius of the dark channel's minimum filter at map resolution (a 15 × 15 patch).</summary>
    private const int PatchRadius = 7;

    /// <summary>How much haze is removed at full strength (0.95 keeps a little, which looks natural).</summary>
    private const float Omega = 0.95f;

    private HazeMap(byte[] transmission, int width, int height, float lightR, float lightG, float lightB)
    {
        Transmission = transmission;
        Width = width;
        Height = height;
        LightR = lightR;
        LightG = lightG;
        LightB = lightB;
    }

    /// <summary>Transmission 0..255 per map pixel, row-major.</summary>
    public byte[] Transmission { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Atmospheric light (the haze colour), linear RGB.</summary>
    public float LightR { get; }
    public float LightG { get; }
    public float LightB { get; }

    /// <summary>The map as a grayscale image for the shader (made on first use).</summary>
    public SKImage Image => _image ??= CreateImage();
    private SKImage? _image;

    private SKImage CreateImage()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var data = SKData.CreateCopy(Transmission);
        return SKImage.FromPixels(info, data, Width) ?? throw new InvalidOperationException("Could not create the haze map image.");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKImage, HazeMap> Cache = new();

    /// <summary>The map of <paramref name="photo"/>, computed once per image object (the viewer draws many frames).</summary>
    public static HazeMap For(SKImage photo) => Cache.GetValue(photo, Compute);

    public static HazeMap Compute(SKImage photo)
    {
        using var bitmap = SKBitmap.FromImage(photo);
        return Compute(bitmap);
    }

    public static HazeMap Compute(SKBitmap photo)
    {
        using var rgba = photo.ColorType == SKColorType.Rgba8888 && photo.AlphaType == SKAlphaType.Premul ? null : photo.Copy(SKColorType.Rgba8888);
        var source = rgba ?? photo;
        var (w, h) = PreviewImage.PreviewSize(source.Width, source.Height, LongSide);
        using var small = PreviewImage.Downscale(source, w, h);
        int n = w * h;
        var r = new float[n];
        var g = new float[n];
        var b = new float[n];
        var guide = new float[n];
        var pixels = small.GetPixelSpan();
        for (int i = 0; i < n; i++)
        {
            byte a = pixels[i * 4 + 3];
            float inv = a > 0 ? 1f / a : 0;
            float sr = pixels[i * 4] * inv, sg = pixels[i * 4 + 1] * inv, sb = pixels[i * 4 + 2] * inv;
            r[i] = ColorMath.SrgbToLinear(Math.Min(sr, 1f));
            g[i] = ColorMath.SrgbToLinear(Math.Min(sg, 1f));
            b[i] = ColorMath.SrgbToLinear(Math.Min(sb, 1f));
            guide[i] = 0.2126f * sr + 0.7152f * sg + 0.0722f * sb;
        }

        // Atmospheric light: among the 0.1 % haziest pixels (highest dark channel), the brightest one.
        var dark = MinFilter(Enumerable.Range(0, n).Select(i => MathF.Min(r[i], MathF.Min(g[i], b[i]))).ToArray(), w, h);
        int count = Math.Max(1, n / 1000);
        int best = Enumerable.Range(0, n).OrderByDescending(i => dark[i]).Take(count)
            .MaxBy(i => ToneCurve.Luminance(r[i], g[i], b[i]));
        float lr = Math.Clamp(r[best], 0.05f, 1f), lg = Math.Clamp(g[best], 0.05f, 1f), lb = Math.Clamp(b[best], 0.05f, 1f);

        // Transmission from the dark channel of I / A, refined with a guided filter so it follows edges.
        var normalized = new float[n];
        for (int i = 0; i < n; i++)
            normalized[i] = MathF.Min(r[i] / lr, MathF.Min(g[i] / lg, b[i] / lb));
        var dn = MinFilter(normalized, w, h);
        var t = new float[n];
        for (int i = 0; i < n; i++)
            t[i] = 1f - Omega * Math.Clamp(dn[i], 0f, 1f);
        var refined = GuidedFilter.Apply(t, guide, w, h, radius: 12, epsilon: 1e-3f);
        var bytes = new byte[n];
        for (int i = 0; i < n; i++)
            bytes[i] = (byte)Math.Clamp((int)MathF.Round(refined[i] * 255f), 0, 255);
        return new HazeMap(bytes, w, h, lr, lg, lb);
    }

    /// <summary>
    /// Transmission at image pixel (<paramref name="x"/>, <paramref name="y"/>) of an image of the given size: the
    /// map stretched over the image with bilinear, edge-clamped sampling at pixel centres (as Skia samples it).
    /// </summary>
    public float Sample(int x, int y, int imageWidth, int imageHeight)
    {
        float u = (x + 0.5f) * Width / imageWidth - 0.5f;
        float v = (y + 0.5f) * Height / imageHeight - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float At(int xi, int yi) => Transmission[Math.Clamp(yi, 0, Height - 1) * Width + Math.Clamp(xi, 0, Width - 1)] / 255f;
        float top = At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx;
        float bottom = At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>
    /// Dehaze on a linear-light pixel: amount &gt; 0 removes haze ((I − A) / t + A, with t weakened by the amount and
    /// kept ≥ 0.1), amount &lt; 0 adds a veil of the haze colour. Mirrored in the shader.
    /// </summary>
    public static void Apply(ref float r, ref float g, ref float b, float t, float amount, float lr, float lg, float lb)
    {
        if (amount > 0f)
        {
            float tt = MathF.Max(1f - amount * (1f - t), 0.1f);
            r = MathF.Max((r - lr) / tt + lr, 0f);
            g = MathF.Max((g - lg) / tt + lg, 0f);
            b = MathF.Max((b - lb) / tt + lb, 0f);
        }
        else if (amount < 0f)
        {
            float k = -amount * 0.6f;
            r += (lr - r) * k;
            g += (lg - g) * k;
            b += (lb - b) * k;
        }
    }

    /// <summary>Minimum over a (2 × <see cref="PatchRadius"/> + 1)² window, separable.</summary>
    private static float[] MinFilter(float[] values, int w, int h)
    {
        var tmp = new float[values.Length];
        var result = new float[values.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = float.MaxValue;
                for (int k = Math.Max(0, x - PatchRadius); k <= Math.Min(w - 1, x + PatchRadius); k++)
                    m = MathF.Min(m, values[y * w + k]);
                tmp[y * w + x] = m;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = float.MaxValue;
                for (int k = Math.Max(0, y - PatchRadius); k <= Math.Min(h - 1, y + PatchRadius); k++)
                    m = MathF.Min(m, tmp[k * w + x]);
                result[y * w + x] = m;
            }
        return result;
    }
}
