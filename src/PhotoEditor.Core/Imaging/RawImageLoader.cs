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
    /// 4-core machine — and visually the same at 100 % on real photos.
    /// </summary>
    private static MagickReadSettings ReadSettings
    {
        get
        {
            var settings = new MagickReadSettings();
            settings.SetDefines(new ImageMagick.Formats.DngReadDefines { InterpolationQuality = ImageMagick.Formats.DngInterpolation.Ppg });
            return settings;
        }
    }

    /// <summary>Decodes the RAW file into an 8-bit RGBA bitmap.</summary>
    public static SKBitmap Load(string path)
    {
        byte[] rgba;
        int width, height;
        try
        {
            using var image = new MagickImage(path, ReadSettings);
            width = (int)image.Width;
            height = (int)image.Height;
            rgba = image.GetPixelsUnsafe().ToByteArray("RGBA")
                ?? throw new InvalidDataException($"Could not read RAW pixels: {path}");
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"Unsupported or corrupt RAW file ({ex.Message})", ex);
        }

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length); // opaque, so premultiplied = straight
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
