using PhotoEditor.Core.Adjustments;
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
    public static void Export(SKBitmap original, AdjustmentSettings settings, string? sourcePath, string destinationPath, ExportOptions options)
    {
        using var rendered = CpuAdjustmentRenderer.Render(original, settings);
        var bytes = Encode(rendered, options);

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
}
