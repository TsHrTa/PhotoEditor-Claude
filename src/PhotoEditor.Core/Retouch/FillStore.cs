using System.Collections.Concurrent;
using System.Security.Cryptography;
using SkiaSharp;

namespace PhotoEditor.Core.Retouch;

/// <summary>
/// The AI fills of Remove spots, kept as PNG files (default <c>%LOCALAPPDATA%\PhotoEditor\fills</c>) named by their
/// content hash, so a spot can refer to its fill by id and every render — preview, export, another session —
/// uses the same pixels.
/// </summary>
public static class FillStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "fills");

    private static string _directory = DefaultDirectory;
    private static readonly ConcurrentDictionary<string, SKBitmap> Loaded = new();

    /// <summary>Where fills are kept (tests use their own folder).</summary>
    public static string Directory
    {
        get => _directory;
        set
        {
            _directory = value;
            Loaded.Clear();
        }
    }

    /// <summary>Stores <paramref name="fill"/> and returns its id.</summary>
    public static string Save(SKBitmap fill)
    {
        using var data = fill.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var id = Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant();
        System.IO.Directory.CreateDirectory(_directory);
        var path = PathOf(id);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        var copy = fill.Copy();
        copy.SetImmutable();
        Loaded[id] = copy;
        return id;
    }

    /// <summary>The fill with this id, or null if it is not (or no longer) stored.</summary>
    public static SKBitmap? Get(string? id)
    {
        if (id is null || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return null;
        if (Loaded.TryGetValue(id, out var bitmap))
            return bitmap;
        var path = PathOf(id);
        if (!File.Exists(path))
            return null;
        try
        {
            var decoded = SKBitmap.Decode(path);
            if (decoded is null)
                return null;
            using var _ = decoded;
            var rgba = decoded.Copy(SKColorType.Rgba8888);
            rgba.SetImmutable();
            return Loaded.GetOrAdd(id, rgba);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string PathOf(string id) => Path.Combine(_directory, id + ".png");
}
