using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Core.Editing;

/// <summary>Contents of a sidecar file: everything needed to re-create the edit.</summary>
public sealed record EditDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public AdjustmentSettings Adjustments { get; init; } = AdjustmentSettings.Default;
    public ImmutableList<Mask> Masks { get; init; } = [];
    public Crop Crop { get; init; } = Crop.None;

    /// <summary>Rotation / flip after the crop.</summary>
    public PhotoOrientation Orientation { get; init; }

    /// <summary>Star rating and pick / reject flag (not part of the edit; null when never set).</summary>
    public PhotoLabels? Labels { get; init; }

    public static EditDocument From(EditState state) =>
        new() { Adjustments = state.Adjustments, Masks = state.Masks, Crop = state.Crop, Orientation = state.Orientation };

    public EditState ToState() => new() { Adjustments = Adjustments, Masks = Masks, Crop = Crop, Orientation = new(Orientation.Turns, Orientation.Flip) };

    public bool Equals(EditDocument? other) =>
        other is not null && Version == other.Version && ToState() == other.ToState();

    public override int GetHashCode() => HashCode.Combine(Version, Adjustments, Masks.Count);
}

/// <summary>
/// Reads and writes edit settings as JSON next to the image: <c>photo.jpg</c> → <c>photo.jpg.json</c>.
/// </summary>
public static class SidecarFile
{
    public const string Extension = ".json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string PathFor(string imagePath) => imagePath + Extension;

    public static string Serialize(EditDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>Parses a sidecar; out-of-range values are clamped. Throws <see cref="JsonException"/> on invalid JSON.</summary>
    public static EditDocument Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<EditDocument>(json, Options) ?? new EditDocument();
        var masks = (doc.Masks ?? [])
            .Where(m => m is not null)
            .Select(m => m with
            {
                Adjustments = Normalize(m.Adjustments ?? AdjustmentSettings.Default),
                Components = (m.Components ?? []).RemoveAll(c => c is null),
            })
            .ToImmutableList();
        return doc with
        {
            Adjustments = Normalize(doc.Adjustments ?? AdjustmentSettings.Default),
            Masks = masks,
            Crop = CropGeometry.Normalize(doc.Crop),
        };
    }

    /// <summary>Writes the sidecar for <paramref name="imagePath"/> (atomically via a temp file).</summary>
    public static void Save(string imagePath, EditDocument document)
    {
        var path = PathFor(imagePath);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(document));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Loads the sidecar for <paramref name="imagePath"/>; null if there is none.</summary>
    public static EditDocument? Load(string imagePath)
    {
        var path = PathFor(imagePath);
        return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
    }

    public static bool Exists(string imagePath) => File.Exists(PathFor(imagePath));

    /// <summary>Clamps every value into its slider range and fills missing HSL bands.</summary>
    private static AdjustmentSettings Normalize(AdjustmentSettings s)
    {
        for (int i = 0; i < HslBands.Count; i++)
        {
            if (HslBands.Get(s, i) is null)
                s = HslBands.With(s, i, HslBand.Zero);
        }
        foreach (var p in AdjustmentParameters.All)
            s = p.Set(s, p.Get(s));
        return s;
    }
}
