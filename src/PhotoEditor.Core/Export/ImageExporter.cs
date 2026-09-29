using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Export;

public enum ExportFormat
{
    Jpeg,
    Png,
}

public sealed record ExportOptions(ExportFormat Format = ExportFormat.Jpeg, int JpegQuality = 90)
{
    /// <summary>Picks the format from the file extension (.png → PNG, otherwise JPEG).</summary>
    public static ExportFormat FormatFromPath(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Png : ExportFormat.Jpeg;
}

/// <summary>Renders the full-resolution image with the CPU pipeline and writes it, keeping the source's EXIF.</summary>
public static class ImageExporter
{
    public static void Export(SKBitmap original, AdjustmentSettings settings, string? sourcePath, string destinationPath, ExportOptions options) =>
        Export(original, new EditState { Adjustments = settings }, sourcePath, destinationPath, options);

    public static void Export(SKBitmap original, EditState state, string? sourcePath, string destinationPath, ExportOptions options)
    {
        if (IsSameFile(sourcePath, destinationPath))
            throw new InvalidOperationException("The export would overwrite the original photo; choose another file name.");
        using var rendered = CpuAdjustmentRenderer.Render(original, state);
        var cropped = CpuAdjustmentRenderer.ApplyCrop(rendered, state.Crop);
        byte[] bytes;
        try
        {
            bytes = Encode(cropped, options);
        }
        finally
        {
            if (!ReferenceEquals(cropped, rendered))
                cropped.Dispose();
        }

        var exif = sourcePath is null ? null : ExifMetadata.Read(sourcePath);
        if (exif is not null)
        {
            // Pixels are already upright (ImageLoader applied the orientation).
            exif = ExifMetadata.WithNormalOrientation(exif);
            bytes = options.Format == ExportFormat.Png
                ? ExifMetadata.EmbedInPng(bytes, exif)
                : ExifMetadata.EmbedInJpeg(bytes, exif);
        }

        // Write to a temp file first so a failed export never leaves a half-written file.
        var temp = destinationPath + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, destinationPath, overwrite: true);
    }

    public static byte[] Encode(SKBitmap bitmap, ExportOptions options)
    {
        using var pixmap = bitmap.PeekPixels();
        using var data = options.Format switch
        {
            ExportFormat.Png => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, zLibLevel: 6)),
            _ => pixmap.Encode(new SKJpegEncoderOptions(
                Math.Clamp(options.JpegQuality, 1, 100),
                options.JpegQuality >= 90 ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420,
                SKJpegEncoderAlphaOption.Ignore)),
        } ?? throw new InvalidOperationException($"Could not encode image as {options.Format}.");
        return data.ToArray();
    }

    /// <summary>True when both paths name the same file (case-insensitive, as on Windows).</summary>
    public static bool IsSameFile(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
