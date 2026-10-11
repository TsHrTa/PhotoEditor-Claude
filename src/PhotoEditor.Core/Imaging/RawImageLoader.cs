using System.Globalization;
using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>Camera information read from a RAW file (null = unknown).</summary>
public sealed record RawMetadata
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public DateTime? DateTaken { get; init; }
    public double? Iso { get; init; }

    /// <summary>Seconds.</summary>
    public double? ExposureTime { get; init; }
    public double? FNumber { get; init; }

    /// <summary>Millimetres.</summary>
    public double? FocalLength { get; init; }

    /// <summary>The lens as the camera names it (e.g. "RF24-105mm F4 L IS USM"), if known.</summary>
    public string? Lens { get; init; }
}

/// <summary>
/// Decodes camera RAW files (Canon CR3/CR2, Nikon NEF, Sony ARW, DNG, …) with LibRaw (Sdcb.LibRaw) and renders them
/// like Lightroom: clipped highlights rebuilt from the channels that weren't clipped, a fixed base curve with a soft
/// highlight shoulder (<see cref="Adjustments.RawBaseCurve"/>), everything above white kept as headroom. Upright.
/// Files it fails on fall back to Magick.NET's LibRaw with LibRaw's own look (<see cref="Develop"/>).
/// </summary>
public static class RawImageLoader
{
    /// <summary>
    /// The demosaicing used for editing and export. RCD (our C# port, <see cref="BayerDemosaic"/>): on the R8 sample
    /// (<c>Bench/DemosaicCompareBench</c>) 5 to 10 % more fine leaf detail than LibRaw's DCB, less false colour in most
    /// regions, and 2.7 s instead of DCB's 6.2 s (single-threaded in LibRaw). AMaZE is as sharp with slightly less false
    /// colour but slower (3.7 s). The quick half-size preview needs no demosaicing.
    /// </summary>
    public const RawDemosaic DefaultDemosaic = RawDemosaic.Rcd;

    public static readonly string[] Extensions =
    [
        ".cr3", ".cr2", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".raf",
        ".orf", ".rw2", ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl", ".erf", ".kdc", ".mrw",
    ];

    public static bool IsRaw(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// The fallback (Magick.NET), PPG demosaicing (faster than AHD: 1.4 s instead of 3.7 s
    /// for a 21 MP CR2 on a 4-core machine — and visually the same at 100 % on real photos. Auto-brightening is off:
    /// LibRaw would clip the brightest 1 % of the photo; <see cref="Develop"/> brightens the same way and keeps what
    /// goes above white.
    /// </summary>
    private static MagickReadSettings ReadSettings
    {
        get
        {
            var settings = new MagickReadSettings();
            settings.SetDefines(new ImageMagick.Formats.DngReadDefines
            {
                InterpolationQuality = ImageMagick.Formats.DngInterpolation.Ppg,
                DisableAutoBrightness = true,
            });
            return settings;
        }
    }

    /// <summary>
    /// Decodes the RAW file into an 8-bit RGBA bitmap that looks as LibRaw's default output; the highlights above
    /// white and the finer shadow steps are attached as its <see cref="Headroom"/>. Upright (the file's orientation
    /// applied).
    /// </summary>
    public static SKBitmap Load(string path) => Load(path, DefaultDemosaic);

    /// <summary>
    /// A half-size (a quarter of the pixels) render of the RAW through the same development as <see cref="Load(string)"/>
    /// (same highlight rebuild, base curve, colour matrix, headroom), several times faster because nothing is demosaiced.
    /// It is what the editor shows while the full decode runs, so the picture does not change tone when the full-size
    /// photo replaces it (the camera's embedded JPEG has the camera's own tone curve). Null when LibRaw cannot read the
    /// file (the full decode then falls back to ImageMagick).
    /// </summary>
    public static SKBitmap? LoadQuick(string path)
    {
        try
        {
            return LoadWithLibRaw(path, DefaultDemosaic, halfSize: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>As <see cref="Load(string)"/> with the given demosaicing algorithm.</summary>
    public static SKBitmap Load(string path, RawDemosaic demosaic)
    {
        try
        {
            return LoadWithLibRaw(path, demosaic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Files the bundled LibRaw doesn't know: ImageMagick's (highlights clipped at white balance, see below).
            return LoadWithMagick(path);
        }
    }

    /// <summary>
    /// LibRaw directly, rendered like Lightroom rather than like LibRaw's default output. LibRaw only demosaics:
    /// linear camera RGB, white-balanced, with nothing clipped before the sensor did ("unclip"). Then
    /// <see cref="DevelopSensor"/> rebuilds the channels the sensor clipped from the ones it didn't, converts to sRGB
    /// and applies a fixed base rendering (<see cref="Adjustments.RawBaseCurve"/>) instead of LibRaw's per-photo
    /// auto-brightening.
    /// </summary>
    private static SKBitmap LoadWithLibRaw(string path, RawDemosaic demosaic, bool halfSize = false) =>
        DecodeWithLibRaw(path, demosaic, halfSize, DevelopSensor);

    /// <summary>
    /// The scene-linear photo before the base rendering: highlights rebuilt, linear sRGB, 1 = where a neutral clips on
    /// the sensor (interleaved RGB floats). For fitting the rendering to references (tests / benches).
    /// </summary>
    public static (float[] Rgb, int Width, int Height) LoadScene(string path, RawDemosaic demosaic) =>
        DecodeWithLibRaw(path, demosaic, false, (rgb, width, height, clip, matrix) =>
        {
            float white = clip.Min();
            var scene = new float[width * height * 3];
            var source = rgb.ToArray();
            var rebuild = HighlightReconstruction.Build(rgb, width, height, clip);
            Parallel.For(0, height, y =>
            {
                Span<float> v = stackalloc float[3];
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 3;
                    v[0] = source[i];
                    v[1] = source[i + 1];
                    v[2] = source[i + 2];
                    rebuild.Apply(v, x, y);
                    for (int c = 0; c < 3; c++)
                        scene[i + c] = Math.Clamp((matrix[c * 3] * v[0] + matrix[c * 3 + 1] * v[1] + matrix[c * 3 + 2] * v[2]) / white,
                            0f, MaxSceneValue);
                }
            });
            return (scene, width, height);
        });

    /// <summary>
    /// LibRaw's output before any development: white-balanced camera RGB (interleaved, 16-bit), the clip level per
    /// channel and the camera → linear sRGB matrix (row-major). For measuring the highlight rebuild (benches).
    /// </summary>
    public static (ushort[] Rgb, int Width, int Height, float[] Clip, float[] Matrix) LoadSensor(string path, RawDemosaic demosaic) =>
        DecodeWithLibRaw(path, demosaic, false, (rgb, width, height, clip, matrix) => (rgb.ToArray(), width, height, clip, matrix));

    private delegate T Developer<T>(ReadOnlySpan<ushort> rgb, int width, int height, float[] clip, float[] matrix);

    private static T DecodeWithLibRaw<T>(string path, RawDemosaic demosaic, bool halfSize, Developer<T> develop)
    {
        bool ownDemosaic = demosaic >= RawDemosaic.BilinearCs && !halfSize;
        using var context = Sdcb.LibRaw.RawContext.OpenFile(path);
        UseCameraCrop(context);
        context.Unpack();
        context.DcrawProcess(o =>
        {
            o.OutputColor = 0; // camera RGB: the highlights are rebuilt before the colour matrix mixes the channels
            o.Gamma[0] = 1;
            o.Gamma[1] = 1;
            o.HighlightMode = 1; // channel multipliers normalised by the largest: nothing clips before the sensor
            o.NoAutoBright = true;
            o.OutputBps = 16;
            o.UserQual = demosaic >= RawDemosaic.BilinearCs ? 0 : (Sdcb.LibRaw.DemosaicAlgorithm)(int)demosaic;
            o.HalfSize = halfSize; // one output pixel per 2 x 2 sensor cells: no demosaicing at all
            if (ownDemosaic)
                o.Interpolation = false; // LibRaw's mosaic as it would interpolate it; BayerDemosaic does that step
        });
        var multipliers = context.PreMultipler.Take(3).ToArray();
        if (multipliers.Length != 3 || multipliers.Any(m => !(m > 0)))
            throw new InvalidDataException("LibRaw gave no white balance multipliers");
        var matrix = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                matrix[i * 3 + j] = context.RgbCamera[i, j];
        using var image = context.MakeDcrawMemoryImage(); // flip applied: upright
        if (image.Channels != 3 || image.Bits != 16)
            throw new InvalidDataException($"Unexpected LibRaw output ({image.Channels} channels, {image.Bits} bits)");
        var rgb = MemoryMarshal.Cast<byte, ushort>(image.AsSpan<byte>());
        float max = multipliers.Max();
        var clip = multipliers.Select(m => 65535f * m / max).ToArray();
        if (ownDemosaic)
        {
            var interpolated = DemosaicMosaic(rgb, image.Width, image.Height, demosaic, clip.Min());
            return develop(interpolated, image.Width, image.Height, clip, matrix);
        }
        return develop(rgb, image.Width, image.Height, clip, matrix);
    }

    /// <summary>
    /// LibRaw's un-interpolated output (each pixel has only its own colour, the others 0; orientation applied) to an
    /// interpolated image with one of <see cref="BayerDemosaic"/>'s methods. The colour of each position of the 2 × 2
    /// pattern is read from the data (after the flip it need not be the sensor's).
    /// </summary>
    private static ushort[] DemosaicMosaic(ReadOnlySpan<ushort> mosaic, int width, int height, RawDemosaic demosaic, float clipPoint)
    {
        var sums = new double[4, 3];
        for (int y = 0; y < Math.Min(height, 256); y++)
            for (int x = 0; x < Math.Min(width, 256); x++)
                for (int c = 0; c < 3; c++)
                    sums[((y & 1) << 1) | (x & 1), c] += mosaic[(y * width + x) * 3 + c];
        var pattern = new byte[4];
        for (int p = 0; p < 4; p++)
        {
            int best = 0;
            for (int c = 1; c < 3; c++)
                if (sums[p, c] > sums[p, best]) best = c;
            pattern[p] = (byte)best;
        }
        if (pattern.Count(c => c == 1) != 2 || !pattern.Contains((byte)0) || !pattern.Contains((byte)2))
            throw new InvalidDataException($"Not a Bayer RGGB-type mosaic ({string.Join("", pattern)})");

        int n = width * height;
        var cfa = new float[n];
        var source = mosaic.ToArray();
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                cfa[i] = source[i * 3 + pattern[((y & 1) << 1) | (x & 1)]];
            }
        });
        float[] r = new float[n], g = new float[n], b = new float[n];
        switch (demosaic)
        {
            case RawDemosaic.Rcd: BayerDemosaic.Rcd(cfa, width, height, pattern, r, g, b); break;
            case RawDemosaic.Amaze: BayerDemosaic.Amaze(cfa, width, height, pattern, clipPoint, r, g, b); break;
            default: BayerDemosaic.Bilinear(cfa, width, height, pattern, r, g, b); break;
        }
        var result = new ushort[n * 3];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                result[i * 3] = (ushort)Math.Clamp(MathF.Round(r[i]), 0f, 65535f);
                result[i * 3 + 1] = (ushort)Math.Clamp(MathF.Round(g[i]), 0f, 65535f);
                result[i * 3 + 2] = (ushort)Math.Clamp(MathF.Round(b[i]), 0f, 65535f);
            }
        });
        return result;
    }

    /// <summary>
    /// LibRaw sizes the Canon R8 image 5999 × 3999 although the camera's own crop (and Lightroom's output) is
    /// 6000 × 4000, the sensor has the extra column and row. When the file's inset crop starts at the active area and is
    /// at most 2 pixels away from LibRaw's size, use the camera's size (set before Unpack, which allocates by it).
    /// </summary>
    private static void UseCameraCrop(Sdcb.LibRaw.RawContext context)
    {
        var handle = context.UnsafeGetHandle();
        var data = Marshal.PtrToStructure<Sdcb.LibRaw.Natives.LibRawData>(handle);
        var sizes = data.ImageSizes;
        var crop = sizes.RawInsetCrops[0];
        if (crop.CWidth == 0 || crop.CHeight == 0 || crop.CLeft != sizes.LeftMargin || crop.CTop != sizes.TopMargin
            || Math.Abs(crop.CWidth - sizes.Width) > 2 || Math.Abs(crop.CHeight - sizes.Height) > 2
            || crop.CLeft + crop.CWidth > sizes.RawWidth || crop.CTop + crop.CHeight > sizes.RawHeight
            || sizes.Width != sizes.IWidth || sizes.Height != sizes.IHeight)
            return;
        int offset = (int)Marshal.OffsetOf<Sdcb.LibRaw.Natives.LibRawData>(nameof(data.ImageSizes));
        int At(string field) => offset + (int)Marshal.OffsetOf<Sdcb.LibRaw.Natives.LibRawImageSizes>(field);
        Marshal.WriteInt16(handle, At(nameof(sizes.Width)), (short)crop.CWidth);
        Marshal.WriteInt16(handle, At(nameof(sizes.IWidth)), (short)crop.CWidth);
        Marshal.WriteInt16(handle, At(nameof(sizes.Height)), (short)crop.CHeight);
        Marshal.WriteInt16(handle, At(nameof(sizes.IHeight)), (short)crop.CHeight);
    }

    /// <summary>The base rendering (<see cref="Adjustments.RawBaseCurve"/>), scene-linear to display-linear.</summary>
    public static float BaseCurve(float x) => Adjustments.RawBaseCurve.Apply(x);

    /// <summary>
    /// Rebuilds the channels the sensor clipped in one pixel (<paramref name="rgb"/>: white-balanced camera RGB, channel c
    /// clipped at <paramref name="clip"/>[c]), without its neighbourhood (see <see cref="HighlightReconstruction"/>, which the
    /// photo is developed with).
    /// </summary>
    public static void ReconstructHighlights(Span<float> rgb, ReadOnlySpan<float> clip) =>
        HighlightReconstruction.Apply(rgb, clip, 0f, 0f, 0f);

    /// <summary>The highlight rebuild over a whole image (interleaved camera RGB), as floats.</summary>
    public static float[] ReconstructAll(ushort[] rgb, int width, int height, float[] clip)
    {
        var result = new float[rgb.Length];
        var rebuild = HighlightReconstruction.Build(rgb, width, height, clip);
        Parallel.For(0, height, y =>
        {
            Span<float> v = stackalloc float[3];
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                v[0] = rgb[i]; v[1] = rgb[i + 1]; v[2] = rgb[i + 2];
                rebuild.Apply(v, x, y);
                result[i] = v[0]; result[i + 1] = v[1]; result[i + 2] = v[2];
            }
        });
        return result;
    }

    /// <summary>
    /// Adobe's hue-preserving RGB tone curve: the curve is applied to the largest and smallest channel and the middle
    /// one is placed between them as before, so a bright colour keeps its hue (a curve per channel shifts it).
    /// </summary>
    public static void ToneRgb(ref float r, ref float g, ref float b)
    {
        ref float hi = ref r, mid = ref g, lo = ref b;
        if (r >= g)
        {
            if (g >= b) { hi = ref r; mid = ref g; lo = ref b; }
            else if (r >= b) { hi = ref r; mid = ref b; lo = ref g; }
            else { hi = ref b; mid = ref r; lo = ref g; }
        }
        else
        {
            if (r >= b) { hi = ref g; mid = ref r; lo = ref b; }
            else if (g >= b) { hi = ref g; mid = ref b; lo = ref r; }
            else { hi = ref b; mid = ref g; lo = ref r; }
        }
        float h = BaseCurve(hi), l = BaseCurve(lo);
        float m = hi > lo ? l + (h - l) * (mid - lo) / (hi - lo) : h;
        hi = h;
        mid = m;
        lo = l;
    }

    /// <summary>Largest scene value (× the sensor's white) the headroom is sized for; brighter stays at its top.</summary>
    private const float MaxSceneValue = 8;

    /// <summary>
    /// Turns LibRaw's linear, white-balanced camera RGB into the photo: highlights rebuilt, camera → linear sRGB
    /// (<paramref name="matrix"/>, row-major), scaled so a neutral clips at 1 (the lowest channel clip level), the
    /// <see cref="BaseCurve"/> (hue-preserving), sRGB-encoded; above white and the 8-bit rounding go into the
    /// <see cref="Headroom"/> layers as in <see cref="Develop"/>.
    /// </summary>
    public static SKBitmap DevelopSensor(ReadOnlySpan<ushort> rgb, int width, int height, float[] clip, float[] matrix)
    {
        float white = clip.Min();
        var rebuild = HighlightReconstruction.Build(rgb, width, height, clip);
        float scale = ColorMathEncode(BaseCurve(MaxSceneValue)) - 1;
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var headroom = new SKBitmap(info);
        var fine = new SKBitmap(info);
        nint photoPtr = bitmap.GetPixels(), extraPtr = headroom.GetPixels(), finePtr = fine.GetPixels();
        unsafe
        {
            fixed (ushort* source = rgb)
            {
                nint sourcePtr = (nint)source;
                Parallel.For(0, height, y =>
                {
                    ushort* src = (ushort*)sourcePtr + (long)y * width * 3;
                    byte* p = (byte*)photoPtr + (long)y * bitmap.RowBytes;
                    byte* h = (byte*)extraPtr + (long)y * headroom.RowBytes;
                    byte* f = (byte*)finePtr + (long)y * fine.RowBytes;
                    Span<float> v = stackalloc float[3];
                    Span<float> o = stackalloc float[3];
                    for (int x = 0; x < width; x++)
                    {
                        v[0] = src[x * 3];
                        v[1] = src[x * 3 + 1];
                        v[2] = src[x * 3 + 2];
                        rebuild.Apply(v, x, y);
                        for (int c = 0; c < 3; c++)
                            o[c] = Math.Clamp((matrix[c * 3] * v[0] + matrix[c * 3 + 1] * v[1] + matrix[c * 3 + 2] * v[2]) / white,
                                0f, MaxSceneValue);
                        ToneRgb(ref o[0], ref o[1], ref o[2]);
                        Adjustments.RawLook.Apply(ref o[0], ref o[1], ref o[2]);
                        for (int c = 0; c < 3; c++)
                        {
                            float e = ColorMathEncode(o[c]);
                            int k = x * 4 + c;
                            if (e >= 1)
                            {
                                p[k] = 255;
                                h[k] = (byte)Math.Clamp(MathF.Round((e - 1) / scale * 255), 0, 255);
                                f[k] = Headroom.FineZero;
                            }
                            else
                            {
                                byte b = (byte)MathF.Round(e * 255);
                                p[k] = b;
                                h[k] = 0;
                                f[k] = (byte)Math.Clamp(MathF.Round(Headroom.FineZero + (e - b / 255f) / Headroom.FineStep), 0, 255);
                            }
                        }
                        p[x * 4 + 3] = h[x * 4 + 3] = f[x * 4 + 3] = 255;
                    }
                });
            }
        }
        Headroom.Attach(bitmap, new Headroom(headroom, scale, fine) { BaseCurve = true });
        return bitmap;
    }

    private static float ColorMathEncode(float linear) => Adjustments.ColorMath.LinearToSrgbFast(linear);

    private static SKBitmap LoadWithMagick(string path)
    {
        ushort[] rgb;
        int width, height;
        try
        {
            using var image = new MagickImage(path, ReadSettings);
            // ImageMagick reads the orientation but leaves the sensor image as it is: turn it upright here, with
            // the orientation the embedded previews use (ImageMagick's only for files that reader doesn't know).
            var origin = ReadOrientation(path);
            if (origin == SKEncodedOrigin.TopLeft && image.Orientation is > OrientationType.TopLeft and <= OrientationType.LeftBottom)
                origin = (SKEncodedOrigin)(int)image.Orientation;
            image.Orientation = (OrientationType)(int)origin;
            image.AutoOrient();
            width = (int)image.Width;
            height = (int)image.Height;
            rgb = image.GetPixelsUnsafe().ToShortArray("RGB")
                ?? throw new InvalidDataException($"Could not read RAW pixels: {path}");
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"Unsupported or corrupt RAW file ({ex.Message})", ex);
        }
        return Develop(rgb, width, height);
    }

    private const double CurveOffset = 0.099296826809444, CurveBreak = 0.018053968510807;

    /// <summary>Fraction of the channel values LibRaw's auto-brightening pushes to white (its auto_bright_thr).</summary>
    private const double AutoBrightClip = 0.01;

    /// <summary>Largest brightening the headroom is sized for (4 stops): anything brighter stays clipped.</summary>
    private const double MaxBrightening = 16;

    /// <summary>LibRaw's output curve (BT.709: gamma 0.45, toe slope 4.5) from linear 0..1.</summary>
    public static double ToCurve(double linear) =>
        linear < CurveBreak ? linear * 4.5 : (1 + CurveOffset) * Math.Pow(linear, 0.45) - CurveOffset;

    /// <summary>Inverse of <see cref="ToCurve"/>.</summary>
    public static double FromCurve(double v) =>
        v < CurveBreak * 4.5 ? v / 4.5 : Math.Pow((v + CurveOffset) / (1 + CurveOffset), 1 / 0.45);

    /// <summary>
    /// The fallback's development (<see cref="LoadWithMagick"/>). Turns LibRaw's un-brightened 16-bit RGB into the
    /// photo: brightened like LibRaw's auto-brightening (the 99th
    /// percentile of the brightest channel becomes white), with the values above white and the fraction of an
    /// 8-bit step below it kept in a <see cref="Headroom"/> attached to the result.
    /// </summary>
    public static SKBitmap Develop(ushort[] rgb, int width, int height)
    {
        int n = width * height;
        // LibRaw: per channel, the linear value (in 8-value bins) above which more than 1 % of the pixels lie.
        double white = 0;
        var toLinear = new int[65536];
        for (int v = 0; v < 65536; v++)
            toLinear[v] = (int)(FromCurve(v / 65535.0) * 65535);
        var histograms = new int[3 * 0x2000];
        Parallel.For(0, height, () => new int[3 * 0x2000], (y, _, local) =>
        {
            for (int i = y * width * 3, end = i + width * 3; i < end; i += 3)
            {
                local[toLinear[rgb[i]] >> 3]++;
                local[0x2000 + (toLinear[rgb[i + 1]] >> 3)]++;
                local[0x4000 + (toLinear[rgb[i + 2]] >> 3)]++;
            }
            return local;
        }, local =>
        {
            lock (histograms)
                for (int i = 0; i < local.Length; i++)
                    histograms[i] += local[i];
        });
        for (int c = 0; c < 3; c++)
        {
            long total = 0;
            int bin = 0x2000;
            while (--bin > 32)
                if ((total += histograms[c * 0x2000 + bin]) > n * AutoBrightClip)
                    break;
            white = Math.Max(white, bin << 3);
        }
        double gain = white > 0 ? Math.Min(65535.0 / white, MaxBrightening) : 1;
        float scale = (float)(ToCurve(gain) - 1);

        // Per 16-bit value: the 8-bit photo value, the stored headroom and the rounded-away fraction.
        var photo = new byte[65536];
        var extra = new byte[65536];
        var fraction = new byte[65536];
        for (int v = 0; v < 65536; v++)
        {
            double e = ToCurve(gain * FromCurve(v / 65535.0));
            photo[v] = (byte)Math.Round(Math.Min(e, 1) * 255);
            extra[v] = e > 1 && scale > 0 ? (byte)Math.Clamp(Math.Round((e - 1) / scale * 255), 0, 255) : (byte)0;
            fraction[v] = e >= 1 ? Headroom.FineZero
                : (byte)Math.Clamp(Math.Round(Headroom.FineZero + (e - photo[v] / 255.0) / Headroom.FineStep), 0, 255);
        }

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var headroom = new SKBitmap(info);
        var fine = new SKBitmap(info);
        nint photoPtr = bitmap.GetPixels(), extraPtr = headroom.GetPixels(), finePtr = fine.GetPixels();
        Parallel.For(0, height, y =>
        {
            unsafe
            {
                byte* p = (byte*)photoPtr + (long)y * bitmap.RowBytes;
                byte* h = (byte*)extraPtr + (long)y * headroom.RowBytes;
                byte* f = (byte*)finePtr + (long)y * fine.RowBytes;
                for (int x = 0, i = y * width * 3; x < width; x++, i += 3)
                {
                    ushort r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                    p[x * 4] = photo[r]; p[x * 4 + 1] = photo[g]; p[x * 4 + 2] = photo[b]; p[x * 4 + 3] = 255;
                    h[x * 4] = extra[r]; h[x * 4 + 1] = extra[g]; h[x * 4 + 2] = extra[b]; h[x * 4 + 3] = 255;
                    f[x * 4] = fraction[r]; f[x * 4 + 1] = fraction[g]; f[x * 4 + 2] = fraction[b]; f[x * 4 + 3] = 255;
                }
            }
        });
        Headroom.Attach(bitmap, new Headroom(headroom, Math.Max(scale, 1e-3f), fine));
        return bitmap;
    }

    /// <summary>
    /// The EXIF orientation of a RAW file (how the sensor image is turned to be upright; <see cref="Load"/> applies
    /// it). Reads IFD0 of TIFF-based files (CR2, NEF, ARW, DNG, …) and the CMT1 box of CR3.
    /// Returns <see cref="SKEncodedOrigin.TopLeft"/> when unknown.
    /// </summary>
    public static SKEncodedOrigin ReadOrientation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[(int)Math.Min(stream.Length, 512 * 1024)];
            stream.ReadExactly(head);
            return OrientationFromHeader(head);
        }
        catch (IOException)
        {
            return SKEncodedOrigin.TopLeft;
        }
    }

    public static SKEncodedOrigin OrientationFromHeader(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> tiff = head;
        if (head.Length > 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            // CR3 (ISO base media): IFD0 is a TIFF structure in the "CMT1" box.
            int box = head.IndexOf("CMT1"u8);
            if (box < 0)
                return SKEncodedOrigin.TopLeft;
            tiff = head[(box + 4)..];
        }
        var value = ExifMetadata.GetOrientation(tiff.ToArray());
        return value is >= 1 and <= 8 ? (SKEncodedOrigin)value.Value : SKEncodedOrigin.TopLeft;
    }

    /// <summary>Reads the camera metadata without decoding the pixels; null if the file can't be read.</summary>
    public static RawMetadata? ReadMetadata(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            return WithLibRawValues(path, ParseMetadata(name => image.GetAttribute(name)));
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>
    /// ImageMagick prints LibRaw's numbers with one significant digit ("3e+01" for a 28 mm focal length, "4" for f/4.5)
    /// and names a Canon RF lens by its focal range ("2e+01-1e+02mm f/0-0"), so lens profiles never matched. LibRaw's
    /// own fields have the exact values and the lens name; they replace ImageMagick's where LibRaw reads the file.
    /// </summary>
    private static RawMetadata WithLibRawValues(string path, RawMetadata metadata)
    {
        try
        {
            using var context = Sdcb.LibRaw.RawContext.OpenFile(path);
            var other = context.ImageOtherParams;
            var lens = context.LensInfo;
            string? lensName = new[] { lens.Lens, lens.MakerNotes.Lens }
                .Select(n => n?.Trim('\0', ' ')).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            return metadata with
            {
                Iso = other.IsoSpeed > 0 ? other.IsoSpeed : metadata.Iso,
                ExposureTime = other.Shutter > 0 ? other.Shutter : metadata.ExposureTime,
                FNumber = other.Aperture > 1 ? Math.Round(other.Aperture, 2) : metadata.FNumber,
                FocalLength = other.FocalLength > 0 ? Math.Round(other.FocalLength, 2) : metadata.FocalLength,
                Lens = lensName ?? metadata.Lens,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return metadata;
        }
    }

    /// <summary>Parses LibRaw's "dng:*" attributes (numbers are printed like "1/2e+01" or "5e+01 mm").</summary>
    public static RawMetadata ParseMetadata(Func<string, string?> attribute)
    {
        string? Text(string name) => attribute(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        double? Positive(string name) => ParseNumber(Text(name)) is > 0 and var v ? v : null;

        return new RawMetadata
        {
            Make = Text("dng:make"),
            Model = Text("dng:camera.model.name"),
            DateTaken = DateTimeOffset.TryParse(Text("dng:create.date"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ? date.DateTime : null,
            Iso = Positive("dng:iso.setting"),
            ExposureTime = Positive("dng:exposure.time"),
            // LibRaw reports 1 (or 0) when the lens does not report the aperture.
            FNumber = Positive("dng:f.number") is > 1 and var f ? f : null,
            FocalLength = Positive("dng:focal.length"),
            Lens = Text("dng:lens"),
        };
    }

    /// <summary>"1/2e+01" → 0.05, "5e+01 mm" → 50; null if not a number.</summary>
    public static double? ParseNumber(string? text)
    {
        if (text is null)
            return null;
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
            return null;
        var parts = token.Split('/');
        if (parts.Length > 2 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return null;
        double value = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture);
        if (parts.Length == 2)
        {
            double d = double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (d == 0)
                return null;
            value /= d;
        }
        return double.IsFinite(value) ? value : null;
    }

    /// <summary>Builds an EXIF TIFF block (without "Exif\0\0") for export, or null if nothing is known.</summary>
    public static byte[]? BuildExif(RawMetadata metadata)
    {
        var profile = new ExifProfile();
        bool any = false;
        if (metadata.Make is { } make) { profile.SetValue(ExifTag.Make, make); any = true; }
        if (metadata.Model is { } model) { profile.SetValue(ExifTag.Model, model); any = true; }
        if (metadata.DateTaken is { } date)
        {
            var text = date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            profile.SetValue(ExifTag.DateTimeOriginal, text);
            profile.SetValue(ExifTag.DateTimeDigitized, text);
            any = true;
        }
        if (metadata.Iso is { } iso) { profile.SetValue(ExifTag.ISOSpeedRatings, [(ushort)Math.Min(iso, ushort.MaxValue)]); any = true; }
        if (metadata.ExposureTime is { } t) { profile.SetValue(ExifTag.ExposureTime, ExposureRational(t)); any = true; }
        if (metadata.FNumber is { } f) { profile.SetValue(ExifTag.FNumber, new Rational((uint)Math.Round(f * 10), 10)); any = true; }
        if (metadata.FocalLength is { } fl) { profile.SetValue(ExifTag.FocalLength, new Rational((uint)Math.Round(fl * 10), 10)); any = true; }
        if (!any)
            return null;

        var bytes = profile.ToByteArray();
        if (bytes is null)
            return null;
        ReadOnlySpan<byte> header = "Exif\0\0"u8;
        return bytes.AsSpan().StartsWith(header) ? bytes[header.Length..] : bytes;
    }

    /// <summary>1/250 s as 1/250, 2.5 s as 25/10.</summary>
    private static Rational ExposureRational(double seconds) =>
        seconds < 1 ? new Rational(1, (uint)Math.Round(1 / seconds)) : new Rational((uint)Math.Round(seconds * 10), 10);
}

/// <summary>LibRaw's demosaicing algorithms (the values of its user_qual option).</summary>
public enum RawDemosaic
{
    Linear = 0,
    Vng = 1,
    Ppg = 2,
    Ahd = 3,
    Dcb = 4,
    Dht = 11,
    Aahd = 12,

    /// <summary>Our own C# methods (<see cref="BayerDemosaic"/>) on LibRaw's mosaic, not LibRaw user_qual values.</summary>
    BilinearCs = 100,
    Rcd = 101,
    Amaze = 102,
}
