using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Editing;
using SkiaSharp;

namespace PhotoEditor.ViewModels;

/// <summary>A photo in the filmstrip.</summary>
public partial class FilmstripItem(string path) : ViewModelBase
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);

    /// <summary>Small preview with the saved edit applied (null until loaded).</summary>
    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>The photo has an edit (JSON or XMP sidecar).</summary>
    [ObservableProperty]
    public partial bool HasEdits { get; set; }

    /// <summary>Star rating and pick / reject flag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Stars), nameof(IsPicked), nameof(IsRejected), nameof(LabelText))]
    public partial PhotoLabels Labels { get; set; } = PhotoLabels.None;

    /// <summary>"★★★" (empty when unrated).</summary>
    public string Stars => new('★', Labels.Rating);

    public bool IsPicked => Labels.Flag == PhotoFlag.Pick;
    public bool IsRejected => Labels.Flag == PhotoFlag.Reject;

    /// <summary>E.g. "★★★☆☆ · Picked".</summary>
    public string LabelText => new string('★', Labels.Rating) + new string('☆', 5 - Labels.Rating)
        + Labels.Flag switch { PhotoFlag.Pick => " · Picked", PhotoFlag.Reject => " · Rejected", _ => "" };

    public bool IsSamePath(string? other) =>
        other is not null && string.Equals(System.IO.Path.GetFullPath(other), System.IO.Path.GetFullPath(Path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Copies an RGBA8888 premultiplied SkiaSharp bitmap into an Avalonia bitmap (any thread).</summary>
    public static Bitmap ToAvalonia(SKBitmap source)
    {
        using var rgba = source.ColorType == SKColorType.Rgba8888 ? null : source.Copy(SKColorType.Rgba8888);
        var input = rgba ?? source;
        var bitmap = new WriteableBitmap(new PixelSize(input.Width, input.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var row = new byte[input.Width * 4];
        for (int y = 0; y < input.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(input.GetPixels() + y * input.RowBytes, row, 0, row.Length);
            System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}
