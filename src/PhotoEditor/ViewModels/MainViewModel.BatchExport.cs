using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Jobs;

namespace PhotoEditor.ViewModels;

/// <summary>Exporting many photos of the folder at once: one background job per photo.</summary>
public partial class MainViewModel
{
    public bool CanBatchExport => HasFolder || FilePath is not null;

    partial void OnHasFolderChanged(bool value) => OnPropertyChanged(nameof(CanBatchExport));

    /// <summary>The export dialog's view model, with the photo sets to choose from.</summary>
    public BatchExportViewModel CreateBatchExport()
    {
        var scopes = new List<ExportScope>();
        if (Photos.Count > 0)
        {
            var shown = Photos.Where(p => Filter.Matches(p.Labels)).Select(p => p.Path).ToList();
            if (Filter != LabelFilter.All)
                scopes.Add(new ExportScope($"Shown in the filmstrip ({SelectedFilter?.Name})", shown));
            scopes.Add(new ExportScope("Picked photos", _allPhotos.Where(p => p.IsPicked).Select(p => p.Path).ToList()));
            scopes.Add(new ExportScope("All but rejected", _allPhotos.Where(p => !p.IsRejected).Select(p => p.Path).ToList()));
            scopes.Add(new ExportScope("All photos in the folder", _allPhotos.Select(p => p.Path).ToList()));
        }
        if (FilePath is { } open)
            scopes.Add(new ExportScope("Only this photo", [open]));
        var folder = FolderPath ?? Path.GetDirectoryName(FilePath) ?? "";
        return new BatchExportViewModel(scopes, Path.Combine(folder, "Export"), JpegQuality);
    }

    /// <summary>Queues one export job per photo of the chosen set.</summary>
    public void StartBatchExport(BatchExportViewModel dialog)
    {
        if (dialog.SelectedScope is not { Photos.Count: > 0 } scope)
            return;
        SaveEdits(); // the open photo's latest edit must be in its sidecar
        var options = dialog.Options();
        var plan = BatchExport.PlanDestinations(scope.Photos, options);
        int queued = 0, skipped = 0;
        foreach (var (photo, destination) in plan)
        {
            if (destination is null)
            {
                skipped++;
                continue;
            }
            Enqueue(new BackgroundJob($"Export – {Path.GetFileName(photo)} → {Path.GetFileName(destination)}", ctx =>
            {
                BatchExport.ExportOne(photo, destination, options.Format, Pipeline,
                    new Progress(p => ctx.Report(p.Fraction, p.Detail is null ? p.Step : $"{p.Step} · {p.Detail}")), ctx.Cancel);
                return null;
            }, key: $"export|{destination}", photoPath: photo));
            queued++;
        }
        Status = $"Exporting {queued} photo{(queued == 1 ? "" : "s")} to {options.Folder} in the background (see Jobs)"
            + (skipped > 0 ? $"; {skipped} skipped because the file exists." : ".");
    }
}
