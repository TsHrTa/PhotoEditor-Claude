using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Library;

/// <summary>The photos of one folder, as the filmstrip shows them.</summary>
public static class PhotoFolder
{
    /// <summary>
    /// Supported photos in <paramref name="folder"/> (not sub-folders), sorted by name. When a camera saved
    /// RAW + JPEG pairs (IMG_0001.CR3 + IMG_0001.JPG), only the RAW is listed unless
    /// <paramref name="hideJpegWithRaw"/> is false.
    /// </summary>
    public static IReadOnlyList<string> List(string folder, bool hideJpegWithRaw = true)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(ImageLoader.IsSupported)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .ToList();
        if (hideJpegWithRaw)
        {
            var rawNames = files.Where(RawImageLoader.IsRaw)
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            files.RemoveAll(f => IsJpeg(f) && rawNames.Contains(Path.GetFileNameWithoutExtension(f)));
        }
        return files
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => RawImageLoader.IsRaw(f) ? 0 : 1)
            .ThenBy(f => Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

    /// <summary>
    /// True when the photo has an edit: in the app's sidecar, or Camera Raw settings in the XMP (e.g. from
    /// Lightroom). A sidecar holding only a rating or flag does not count.
    /// </summary>
    public static bool HasEdits(string imagePath)
    {
        try
        {
            if (SidecarFile.Load(imagePath) is { } document)
                return !document.ToState().IsDefault;
            return LightroomXmp.HasSettings(imagePath);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return true; // something is there, even if it can't be read
        }
    }

    /// <summary>
    /// The photo's thumbnail with its saved edit applied (adjustments, masks, crop; not AI denoise / deblur,
    /// which would take far too long for a thumbnail); null if the photo can't be read.
    /// </summary>
    public static SKBitmap? EditedThumbnail(ThumbnailCache cache, string imagePath)
    {
        var thumbnail = cache.Get(imagePath);
        if (thumbnail is null || !HasEdits(imagePath))
            return thumbnail;
        try
        {
            var geometry = new ImageGeometry(thumbnail.Width, thumbnail.Height, ImageLoader.ReadOrientation(imagePath),
                RawImageLoader.IsRaw(imagePath));
            var (state, _) = EditStore.Load(imagePath, geometry);
            if (state.IsDefault)
                return thumbnail;
            var rendered = CpuAdjustmentRenderer.Render(thumbnail, state, Lens.PhotoLens.Of(imagePath));
            var cropped = CpuAdjustmentRenderer.ApplyCrop(rendered, state.Crop);
            if (!ReferenceEquals(cropped, rendered))
                rendered.Dispose();
            var oriented = state.Orientation.Apply(cropped);
            if (!ReferenceEquals(oriented, cropped))
                cropped.Dispose();
            thumbnail.Dispose();
            return oriented;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or FormatException or System.Xml.XmlException or InvalidDataException)
        {
            return thumbnail; // a broken sidecar: show the photo unedited
        }
    }
}
