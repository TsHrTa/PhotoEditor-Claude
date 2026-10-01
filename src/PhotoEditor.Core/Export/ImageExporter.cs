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

/// <param name="LongEdge">Downscale so the long side is at most this many pixels (null = full size).</param>
public sealed record ExportOptions(ExportFormat Format = ExportFormat.Jpeg, int JpegQuality = 90, int? LongEdge = null)
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
        // Float from here to the encoder: crop, rotation and resizing work on the unrounded result and the only rounding
        // to 8 bits (with dither) is the last step.
        using var rendered = CpuAdjustmentRenderer.RenderFloat(original, state, Lens.PhotoLens.Of(sourcePath));
        var cropped = CpuAdjustmentRenderer.ApplyCrop(rendered, state.Crop);
        var oriented = state.Orientation.Apply(cropped);
        var sized = options.LongEdge is { } edge ? Resize(oriented, edge) : oriented;
        byte[] bytes;
        try
        {
            bytes = Encode(sized, options);
        }
        finally
        {
            if (!ReferenceEquals(sized, oriented))
                sized.Dispose();
            if (!ReferenceEquals(oriented, cropped))
                oriented.Dispose();
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

    /// <summary>Downscales (high quality) so the long side is at most <paramref name="longEdge"/>; returns the input if it fits.</summary>
    public static SKBitmap Resize(SKBitmap source, int longEdge)
    {
        var (w, h) = PreviewImage.PreviewSize(source.Width, source.Height, Math.Max(1, longEdge));
        if (w == source.Width && h == source.Height)
            return source;
        return source.Resize(new SKImageInfo(w, h, source.ColorType, SKAlphaType.Premul),
                   new SKSamplingOptions(new SKCubicResampler(1f / 3, 1f / 3)))
               ?? throw new InvalidOperationException("Could not resize the image.");
    }

    public static byte[] Encode(SKBitmap bitmap, ExportOptions options)
    {
        // A float result is rounded to 8 bits here, with dither.
        using var bytes = bitmap.ColorType == SKColorType.RgbaF16 ? FloatBitmap.ToBytes(bitmap) : null;
        var pixels = bytes ?? bitmap;
        // Tagged sRGB: the file carries an sRGB profile, so viewers that manage colour show what the editor showed.
        using var srgb = SKColorSpace.CreateSrgb();
        using var pixmap = new SKPixmap(pixels.Info.WithColorSpace(srgb), pixels.GetPixels(), pixels.RowBytes);
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
