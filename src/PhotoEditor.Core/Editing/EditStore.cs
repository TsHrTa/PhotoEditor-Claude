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
    /// <summary>
    /// The app's JSON sidecar if there is one, otherwise Camera Raw settings from an XMP sidecar, otherwise the
    /// photo's default (<see cref="EditState.DefaultFor"/>: RAWs start with some sharpening and noise reduction).
    /// </summary>
    public static (EditState State, EditSource Source) Load(string imagePath, ImageGeometry geometry)
    {
        // A JSON holding only a rating / flag is not an edit: then look at the XMP (e.g. edited in Lightroom).
        if (SidecarFile.Load(imagePath) is { } doc && doc.IsEditFor(geometry.IsRaw))
            return (doc.ToState(), EditSource.Json);
        if (LightroomXmp.Load(imagePath, geometry) is { } imported && !imported.IsDefaultFor(geometry.IsRaw))
            return (imported, EditSource.Xmp);
        return (EditState.DefaultFor(geometry.IsRaw), EditSource.None);
    }

    /// <summary>
    /// Writes the JSON and XMP sidecars (none for an unedited photo that has none yet). Returns what the
    /// Lightroom XMP could not include.
    /// </summary>
    public static IReadOnlyList<string> Save(string imagePath, EditState state, ImageGeometry geometry)
    {
        bool edited = !state.IsDefaultFor(geometry.IsRaw);
        if (edited || SidecarFile.Exists(imagePath))
        {
            SidecarFile.Save(imagePath, EditDocument.From(state) with
            {
                Labels = ExistingLabels(imagePath), Edited = edited ? true : null,
            });
        }
        if (edited || File.Exists(LightroomXmp.PathFor(imagePath)))
            return LightroomXmp.Save(imagePath, state, geometry);
        return [];
    }

    /// <summary>The labels in the existing JSON sidecar (kept when the edit is saved); null if none or unreadable.</summary>
    private static PhotoLabels? ExistingLabels(string imagePath)
    {
        try
        {
            return SidecarFile.Load(imagePath)?.Labels;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The photo's rating and flag: from the JSON sidecar, else the rating in the XMP (e.g. set in Lightroom).</summary>
    public static PhotoLabels LoadLabels(string imagePath)
    {
        try
        {
            if (SidecarFile.Load(imagePath)?.Labels is { } labels)
                return labels;
        }
        catch (System.Text.Json.JsonException)
        {
            // fall back to the XMP
        }
        return LightroomXmp.ReadRating(imagePath) switch
        {
            < 0 => new PhotoLabels(0, PhotoFlag.Reject),
            int stars => new PhotoLabels(Math.Clamp(stars, 0, 5)),
            null => PhotoLabels.None,
        };
    }

    /// <summary>
    /// Saves the rating and flag into the JSON sidecar (keeping the edit) and the rating into the XMP, which
    /// Lightroom reads (a reject is written as rating −1, Adobe's convention).
    /// </summary>
    public static void SaveLabels(string imagePath, PhotoLabels labels)
    {
        EditDocument document;
        try
        {
            document = SidecarFile.Load(imagePath) ?? new EditDocument();
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException($"The edit file {Path.GetFileName(SidecarFile.PathFor(imagePath))} is damaged; not overwriting it.");
        }
        SidecarFile.Save(imagePath, document with { Labels = labels });
        LightroomXmp.SaveRating(imagePath, labels.Flag == PhotoFlag.Reject ? -1 : labels.Rating);
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
