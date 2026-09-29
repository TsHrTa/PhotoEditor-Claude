using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Small upright thumbnails of photos (unedited), made from the RAW's embedded preview or a fast scaled JPEG
/// decode and kept as JPEGs on disk so a folder opens instantly the second time. Safe to use from several threads.
/// </summary>
public sealed class ThumbnailCache(string directory, int longSide = ThumbnailCache.DefaultLongSide)
{
    public const int DefaultLongSide = 320;

    /// <summary>Bump when thumbnails are made differently, so old ones are recreated.</summary>
    public const int Version = 1;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "cache", "thumbs");

    public string Directory { get; } = directory;
    public int LongSide { get; } = longSide;

    /// <summary>Cache file for the photo (changes when the photo file changes).</summary>
    public string PathFor(string imagePath)
    {
        var info = new FileInfo(imagePath);
        var key = $"{Path.GetFullPath(imagePath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{LongSide}|v{Version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Directory, hash + ".jpg");
    }

    /// <summary>The thumbnail from the cache, or made now and cached; null if the photo can't be read.</summary>
    public SKBitmap? Get(string imagePath)
    {
        string cached;
        try
        {
            cached = PathFor(imagePath);
            if (File.Exists(cached) && SKBitmap.Decode(cached) is { } hit)
            {
                using (hit)
                    return hit.Copy(SKColorType.Rgba8888);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var thumbnail = Create(imagePath, LongSide);
        if (thumbnail is not null)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var temp = $"{cached}.{Environment.CurrentManagedThreadId}.tmp";
                using (var data = thumbnail.Encode(SKEncodedImageFormat.Jpeg, 85))
                using (var file = File.Create(temp))
                    data.SaveTo(file);
                File.Move(temp, cached, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not fatal: made again next time.
            }
        }
        return thumbnail;
    }

    /// <summary>
    /// An upright thumbnail at most <paramref name="longSide"/> px: from the embedded preview for RAW files (full
    /// decode only when there is none), a fast scaled decode for JPEG and other formats.
    /// </summary>
    public static SKBitmap? Create(string imagePath, int longSide)
    {
        try
        {
            if (RawImageLoader.IsRaw(imagePath))
            {
                if (EmbeddedPreview.Load(imagePath, longSide) is { } preview)
                    return preview;
                using var full = RawImageLoader.Load(imagePath);
                var small = EmbeddedPreview.FitLongSide(full, longSide);
                return ReferenceEquals(small, full) ? full.Copy() : small;
            }

            var decoded = EmbeddedPreview.DecodeScaled(File.ReadAllBytes(imagePath), longSide);
            if (decoded is null)
                return null;
            var fitted = EmbeddedPreview.FitLongSide(decoded, longSide);
            if (!ReferenceEquals(fitted, decoded))
                decoded.Dispose();
            var upright = ImageLoader.ApplyOrientation(fitted, ImageLoader.ReadOrientation(imagePath));
            if (!ReferenceEquals(upright, fitted))
                fitted.Dispose();
            return upright;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
