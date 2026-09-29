using System.Text.Json.Serialization;

namespace PhotoEditor.Core.Editing;

/// <summary>Culling flag, as in Lightroom.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PhotoFlag>))]
public enum PhotoFlag
{
    None,
    Pick,
    Reject,
}

/// <summary>Star rating (0–5) and pick / reject flag of a photo. Stored next to the edit, but not part of it (no undo).</summary>
public sealed record PhotoLabels(int Rating = 0, PhotoFlag Flag = PhotoFlag.None)
{
    public static readonly PhotoLabels None = new();

    [JsonIgnore]
    public bool IsDefault => this == None;
}

/// <summary>Which photos the filmstrip shows.</summary>
public enum LabelFilter
{
    All,
    Picked,
    NotRejected,
    Rejected,
    Unrated,
    OneStarPlus,
    TwoStarsPlus,
    ThreeStarsPlus,
    FourStarsPlus,
    FiveStars,
}

public static class LabelFilters
{
    public static bool Matches(this LabelFilter filter, PhotoLabels labels) => filter switch
    {
        LabelFilter.Picked => labels.Flag == PhotoFlag.Pick,
        LabelFilter.NotRejected => labels.Flag != PhotoFlag.Reject,
        LabelFilter.Rejected => labels.Flag == PhotoFlag.Reject,
        LabelFilter.Unrated => labels.Rating == 0,
        LabelFilter.OneStarPlus => labels.Rating >= 1,
        LabelFilter.TwoStarsPlus => labels.Rating >= 2,
        LabelFilter.ThreeStarsPlus => labels.Rating >= 3,
        LabelFilter.FourStarsPlus => labels.Rating >= 4,
        LabelFilter.FiveStars => labels.Rating >= 5,
        _ => true,
    };

    public static string DisplayName(this LabelFilter filter) => filter switch
    {
        LabelFilter.Picked => "Picked",
        LabelFilter.NotRejected => "Not rejected",
        LabelFilter.Rejected => "Rejected",
        LabelFilter.Unrated => "Unrated",
        LabelFilter.OneStarPlus => "★ and more",
        LabelFilter.TwoStarsPlus => "★★ and more",
        LabelFilter.ThreeStarsPlus => "★★★ and more",
        LabelFilter.FourStarsPlus => "★★★★ and more",
        LabelFilter.FiveStars => "★★★★★",
        _ => "All photos",
    };
}
