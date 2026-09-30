using System.Collections.Concurrent;
using ImageMagick;
using PhotoEditor.Core.Imaging;

namespace PhotoEditor.Core.Lens;

/// <summary>What a photo's file says about how it was taken: camera, lens, focal length and aperture.</summary>
public sealed record PhotoLens(string? CameraMaker, string? CameraModel, string? LensName, double? FocalLength, double? Aperture,
    double? FocalLength35mm = null)
{
    public static readonly PhotoLens Unknown = new(null, null, null, null, null);

    private static readonly ConcurrentDictionary<string, PhotoLens> Cache = new(StringComparer.Ordinal);

    /// <summary>The lens information of the photo at <paramref name="path"/> (read once, cached); unknown fields are null.</summary>
    public static PhotoLens Of(string? path)
    {
        if (path is null)
            return Unknown;
        return Cache.GetOrAdd(Path.GetFullPath(path), Read);
    }

    private static PhotoLens Read(string path)
    {
        try
        {
            if (RawImageLoader.IsRaw(path))
            {
                if (RawImageLoader.ReadMetadata(path) is not { } raw)
                    return Unknown;
                return new PhotoLens(raw.Make, raw.Model, raw.Lens, raw.FocalLength, raw.FNumber);
            }
            return FromExif(ExifMetadata.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MagickException or InvalidDataException)
        {
            return Unknown;
        }
    }

    /// <summary>Reads the lens information from an EXIF TIFF block (as <see cref="ExifMetadata.Read(string)"/> returns it).</summary>
    public static PhotoLens FromExif(byte[]? tiff)
    {
        if (tiff is null)
            return Unknown;
        var profile = new ExifProfile([.. "Exif\0\0"u8, .. tiff]);
        string? Text(ExifTag<string> tag) => profile.GetValue(tag)?.Value?.Trim('\0', ' ') is { Length: > 0 } v ? v : null;
        double? Ratio(ExifTag<Rational> tag) => profile.GetValue(tag)?.Value is { Denominator: > 0 } r && r.ToDouble() > 0 ? r.ToDouble() : null;
        double? focal35 = profile.GetValue(ExifTag.FocalLengthIn35mmFilm)?.Value is > 0 and var f ? f : null;
        return new PhotoLens(Text(ExifTag.Make), Text(ExifTag.Model), Text(ExifTag.LensModel), Ratio(ExifTag.FocalLength),
            Ratio(ExifTag.FNumber) is > 1 and var a ? a : null, focal35);
    }
}
