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
/// Decodes camera RAW files (Canon CR3/CR2, Nikon NEF, Sony ARW, DNG, …) with LibRaw via Magick.NET.
/// LibRaw demosaics with the camera white balance into sRGB and already rotates the pixels upright.
/// </summary>
public static class RawImageLoader
{
    public static readonly string[] Extensions =
    [
        ".cr3", ".cr2", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".raf",
        ".orf", ".rw2", ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl", ".erf", ".kdc", ".mrw",
    ];

    public static bool IsRaw(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// PPG demosaicing: 2.5× faster than LibRaw's default (AHD) — 1.4 s instead of 3.7 s for a 21 MP CR2 on a
    /// 4-core machine — and visually the same at 100 % on real photos. Auto-brightening is off: LibRaw would
    /// clip the brightest 1 % of the photo; <see cref="Develop"/> brightens the same way and keeps what goes above white.
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
    /// white and the finer shadow steps are attached as its <see cref="Headroom"/>.
    /// </summary>
    public static SKBitmap Load(string path)
    {
        ushort[] rgb;
        int width, height;
        try
        {
            using var image = new MagickImage(path, ReadSettings);
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
    /// Turns LibRaw's un-brightened 16-bit RGB into the photo: brightened like LibRaw's auto-brightening (the 99th
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
    /// The EXIF orientation of a RAW file (how the sensor image is turned to be upright; LibRaw applies it
    /// when decoding). Reads IFD0 of TIFF-based files (CR2, NEF, ARW, DNG, …) and the CMT1 box of CR3.
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
            return ParseMetadata(name => image.GetAttribute(name));
        }
        catch (MagickException)
        {
            return null;
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
