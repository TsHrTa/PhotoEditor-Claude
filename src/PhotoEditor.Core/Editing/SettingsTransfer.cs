using System.Collections.Immutable;
using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.Core.Editing;

/// <summary>Parts of an edit that can be copied to other photos.</summary>
[Flags]
public enum SettingsGroups
{
    None = 0,

    /// <summary>Exposure, contrast, highlights, shadows, whites, blacks.</summary>
    Light = 1,

    /// <summary>Temperature, tint, vibrance, saturation.</summary>
    Color = 2,

    /// <summary>The eight HSL colour bands.</summary>
    Hsl = 4,

    Vignette = 8,

    /// <summary>Sharpening.</summary>
    Detail = 16,

    Crop = 32,

    /// <summary>All masks with their adjustments.</summary>
    Masks = 64,

    /// <summary>What "Copy settings" selects initially (crop and masks usually belong to one photo).</summary>
    Default = Light | Color | Hsl | Vignette | Detail,

    All = Default | Crop | Masks,
}

/// <summary>Copies selected parts of one photo's edit onto another's.</summary>
public static class SettingsTransfer
{
    /// <summary>
    /// <paramref name="target"/> with the <paramref name="groups"/> of <paramref name="source"/>.
    /// A copied crop is refitted to the target's size (<paramref name="targetWidth"/> × <paramref name="targetHeight"/>)
    /// so a straightened frame stays inside the photo. Copied masks get new ids.
    /// </summary>
    public static EditState Apply(EditState target, EditState source, SettingsGroups groups, int targetWidth, int targetHeight)
    {
        var a = target.Adjustments;
        var s = source.Adjustments;
        if (groups.HasFlag(SettingsGroups.Light))
            a = a with { Exposure = s.Exposure, Contrast = s.Contrast, Highlights = s.Highlights, Shadows = s.Shadows, Whites = s.Whites, Blacks = s.Blacks };
        if (groups.HasFlag(SettingsGroups.Color))
            a = a with { Temperature = s.Temperature, Tint = s.Tint, Vibrance = s.Vibrance, Saturation = s.Saturation };
        if (groups.HasFlag(SettingsGroups.Hsl))
        {
            for (int i = 0; i < HslBands.Count; i++)
                a = HslBands.With(a, i, HslBands.Get(s, i));
        }
        if (groups.HasFlag(SettingsGroups.Vignette))
            a = a with { VignetteAmount = s.VignetteAmount, VignetteMidpoint = s.VignetteMidpoint, VignetteRoundness = s.VignetteRoundness, VignetteFeather = s.VignetteFeather };
        if (groups.HasFlag(SettingsGroups.Detail))
            a = a with { SharpenAmount = s.SharpenAmount, SharpenRadius = s.SharpenRadius, SharpenMasking = s.SharpenMasking };

        var result = target with { Adjustments = a };
        if (groups.HasFlag(SettingsGroups.Crop))
        {
            var crop = source.Crop;
            if (!crop.Frame(targetWidth, targetHeight).IsInside(targetWidth, targetHeight))
                crop = CropGeometry.WithAngle(crop, crop.Angle, targetWidth, targetHeight);
            result = result with { Crop = crop };
        }
        if (groups.HasFlag(SettingsGroups.Masks))
            result = result with { Masks = source.Masks.Select(m => m with { Id = Guid.NewGuid() }).ToImmutableList() };
        return result;
    }

    /// <summary>
    /// Pastes into a photo that is not open: reads its current edit from its sidecars, applies the groups and
    /// writes the sidecars back (the photo file itself is not touched).
    /// </summary>
    public static PasteResult PasteToFile(string imagePath, EditState source, SettingsGroups groups)
    {
        try
        {
            var geometry = EditStore.ReadGeometry(imagePath);
            var (current, _) = EditStore.Load(imagePath, geometry);
            var next = Apply(current, source, groups, geometry.Width, geometry.Height);
            return new PasteResult(imagePath, null, EditStore.Save(imagePath, next, geometry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or FormatException)
        {
            return new PasteResult(imagePath, ex.Message, []);
        }
    }
}

/// <summary>Outcome of pasting into one photo: <see cref="Error"/> is null on success.</summary>
public sealed record PasteResult(string ImagePath, string? Error, IReadOnlyList<string> Skipped);
