using ImageMagick;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Editing;

/// <summary>Where a loaded edit came from.</summary>
public enum EditSource
{
    None,

    /// <summary>The app's own JSON sidecar.</summary>
    Json,

    /// <summary>Imported from a Lightroom / Camera Raw XMP sidecar.</summary>
    Xmp,
}

/// <summary>
/// Loads and saves the edit of a photo through its sidecar files (the app's JSON plus a Lightroom XMP).
/// The photo itself is never written.
/// </summary>
public static class EditStore
{
    /// <summary>The app's JSON sidecar if there is one, otherwise Camera Raw settings from an XMP sidecar.</summary>
    public static (EditState State, EditSource Source) Load(string imagePath, ImageGeometry geometry)
    {
        if (SidecarFile.Load(imagePath) is { } doc)
            return (doc.ToState(), EditSource.Json);
        if (LightroomXmp.Load(imagePath, geometry) is { IsDefault: false } imported)
            return (imported, EditSource.Xmp);
        return (EditState.Default, EditSource.None);
    }

    /// <summary>
    /// Writes the JSON and XMP sidecars (none for an unedited photo that has none yet). Returns what the
    /// Lightroom XMP could not include.
    /// </summary>
    public static IReadOnlyList<string> Save(string imagePath, EditState state, ImageGeometry geometry)
    {
        if (!state.IsDefault || SidecarFile.Exists(imagePath))
            SidecarFile.Save(imagePath, EditDocument.From(state));
        if (!state.IsDefault || File.Exists(LightroomXmp.PathFor(imagePath)))
            return LightroomXmp.Save(imagePath, state, geometry);
        return [];
    }

    /// <summary>Size (upright, as the editor shows it) and orientation of a photo, read without decoding the pixels.</summary>
    public static ImageGeometry ReadGeometry(string imagePath)
    {
        bool isRaw = RawImageLoader.IsRaw(imagePath);
        var orientation = ImageLoader.ReadOrientation(imagePath);
        int width, height;
        if (isRaw)
        {
            try
            {
                using var image = new MagickImage();
                image.Ping(imagePath);
                (width, height) = ((int)image.Width, (int)image.Height);
            }
            catch (MagickException ex)
            {
                throw new InvalidDataException($"Unsupported or corrupt RAW file ({ex.Message})", ex);
            }
        }
        else
        {
            using var codec = SKCodec.Create(imagePath) ?? throw new InvalidDataException($"Unsupported or corrupt image: {imagePath}");
            (width, height) = (codec.Info.Width, codec.Info.Height);
        }

        var geometry = new ImageGeometry(width, height, orientation, isRaw);
        // Stored sizes are sensor-oriented; the editor works on the upright image. (LibRaw may report either,
        // so for RAW only swap when the reported size is still landscape.)
        bool swap = geometry.IsQuarterTurn && (!isRaw || width > height);
        return swap ? geometry with { Width = height, Height = width } : geometry;
    }
}
