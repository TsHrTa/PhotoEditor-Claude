using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace PhotoEditor.Core.Masks;

/// <summary>A click (normalised image coordinates): <see cref="Include"/> = part of the object, false = not.</summary>
public sealed record SelectPoint(float X, float Y, bool Include = true);

/// <summary>A box around the object (normalised image coordinates).</summary>
public sealed record SelectBox(float Left, float Top, float Right, float Bottom);

/// <summary>
/// A mask computed by an AI model and stored as a grayscale raster (PNG, stretched over the whole image).
/// For click / box selection the prompts are kept so the selection can be refined later.
/// </summary>
public sealed record RasterMaskComponent : MaskComponent
{
    /// <summary>"object" (click / box), later "subject", "sky", "people".</summary>
    public string Source { get; init; } = "object";

    public ImmutableList<SelectPoint> Points { get; init; } = [];
    public SelectBox? Box { get; init; }

    /// <summary>8-bit grayscale PNG of the coverage (any size; it is stretched over the image).</summary>
    public byte[] MaskPng { get; init; } = [];

    [JsonIgnore]
    public override string DisplayName => Source switch
    {
        "subject" => "Subject",
        "sky" => "Sky",
        "people" => "People",
        _ => "Object",
    };

    private static readonly ConditionalWeakTable<byte[], SKBitmap> Decoded = new();

    /// <summary>The decoded raster (cached per PNG instance); null if there is none.</summary>
    private SKBitmap? Raster()
    {
        if (MaskPng.Length == 0)
            return null;
        return Decoded.GetValue(MaskPng, png =>
        {
            using var decoded = SKBitmap.Decode(png) ?? throw new InvalidDataException("Invalid mask image.");
            var gray = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Gray8, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(gray))
                canvas.DrawBitmap(decoded, 0, 0);
            return gray;
        });
    }

    public override void Render(Span<float> coverage, int width, int height)
    {
        var raster = Raster();
        if (raster is null)
        {
            coverage[..(width * height)].Clear();
            return;
        }
        using var scaled = raster.Width == width && raster.Height == height
            ? null
            : raster.Resize(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        var source = scaled ?? raster;
        var pixels = source.GetPixelSpan();
        int rowBytes = source.RowBytes;
        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width);
            var target = coverage.Slice(y * width, width);
            for (int x = 0; x < width; x++)
                target[x] = row[x] / 255f;
        }
    }

    /// <summary>Encodes coverage values (0..1, row-major) as the grayscale PNG stored in <see cref="MaskPng"/>.</summary>
    public static byte[] EncodePng(ReadOnlySpan<float> coverage, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[y * rowBytes + x] = (byte)(Math.Clamp(coverage[y * width + x], 0f, 1f) * 255f + 0.5f);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Could not encode the mask.");
        return data.ToArray();
    }

    public bool Equals(RasterMaskComponent? other) =>
        other is not null && base.Equals(other) && Source == other.Source && Points.SequenceEqual(other.Points)
        && Box == other.Box && MaskPng.AsSpan().SequenceEqual(other.MaskPng);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Source, Points.Count, MaskPng.Length);
}
