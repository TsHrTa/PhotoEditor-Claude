using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;

namespace PhotoEditor.Core.Export;

/// <summary>What to do when an export file already exists.</summary>
public enum ExistingFile
{
    /// <summary>Replace it (e.g. an earlier export of the same photo).</summary>
    Replace,

    /// <summary>Keep it and add a number to the new file's name.</summary>
    KeepBoth,

    /// <summary>Don't export this photo.</summary>
    Skip,
}

/// <param name="Folder">Where the files go.</param>
/// <param name="Suffix">Added to the photo's name, e.g. "-edited" → IMG_0001-edited.jpg.</param>
public sealed record BatchExportOptions(string Folder, ExportOptions Format, string Suffix = "", ExistingFile IfExists = ExistingFile.Replace);

/// <summary>Exporting many photos with their saved edits (one job per photo in the app).</summary>
public static class BatchExport
{
    /// <summary>
    /// The export file of each photo (null = skipped because it exists). Names are unique within the batch and
    /// never one of the source photos, so an export can never overwrite an original.
    /// </summary>
    public static IReadOnlyList<(string Photo, string? Destination)> PlanDestinations(IReadOnlyList<string> photos, BatchExportOptions options)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var sources = photos.Select(p => Path.GetFullPath(p)).ToHashSet(comparer);
        var taken = new HashSet<string>(comparer);
        string extension = options.Format.Format switch { ExportFormat.Png => ".png", ExportFormat.Tiff => ".tif", _ => ".jpg" };
        var plan = new List<(string, string?)>();
        foreach (var photo in photos)
        {
            string stem = Path.GetFileNameWithoutExtension(photo) + options.Suffix;
            string Candidate(int n) => Path.GetFullPath(Path.Combine(options.Folder, n == 1 ? stem + extension : $"{stem} ({n}){extension}"));
            bool Blocked(string path) => sources.Contains(path) || taken.Contains(path);

            string destination = Candidate(1);
            int number = 1;
            if (options.IfExists == ExistingFile.KeepBoth)
            {
                while (Blocked(destination) || File.Exists(destination))
                    destination = Candidate(++number);
            }
            else
            {
                while (Blocked(destination))
                    destination = Candidate(++number); // a source photo or another photo of this batch has the name
                if (options.IfExists == ExistingFile.Skip && File.Exists(destination))
                {
                    plan.Add((photo, null));
                    continue;
                }
            }
            taken.Add(destination);
            plan.Add((photo, destination));
        }
        return plan;
    }

    /// <summary>
    /// Decodes <paramref name="photo"/>, applies its saved edit (incl. AI denoise / deblur through
    /// <paramref name="pipeline"/>) and writes <paramref name="destination"/> with the photo's EXIF.
    /// </summary>
    public static void ExportOne(string photo, string destination, ExportOptions format, RestorePipeline pipeline,
        IProgress<RestoreProgress>? progress = null, CancellationToken cancel = default)
    {
        progress?.Report(new RestoreProgress("Decoding", null));
        using var original = ImageLoader.Load(photo);
        cancel.ThrowIfCancellationRequested();
        var geometry = EditStore.ReadGeometry(photo) with { Width = original.Width, Height = original.Height };
        var (state, _) = EditStore.Load(photo, geometry);
        var source = pipeline.Working(photo, original, state.Adjustments.DenoiseAmount / 100, state.Adjustments.DeblurAmount / 100,
            progress, cancel);
        try
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new RestoreProgress("Rendering", null));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ImageExporter.Export(source, state, photo, destination, format);
        }
        finally
        {
            if (!ReferenceEquals(source, original))
                source.Dispose();
        }
    }
}
