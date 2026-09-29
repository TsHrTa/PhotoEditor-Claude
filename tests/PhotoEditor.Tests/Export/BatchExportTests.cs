using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Ai;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Export;
using SkiaSharp;

namespace PhotoEditor.Tests.Export;

public sealed class BatchExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("batch-export-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Photo(string name, byte gray = 80, int width = 400, int height = 300)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(new SKColor(gray, gray, gray));
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, ImageExporter.Encode(bmp, new ExportOptions(ExportFormat.Jpeg, 95)));
        return path;
    }

    private RestorePipeline Pipeline() =>
        new(new ModelStore(Path.Combine(_dir, "models"), new HttpClient()), new RestoreCache(Path.Combine(_dir, "cache")));

    [Fact]
    public void Plan_NeverOverwritesASourcePhoto_AndKeepsNamesUniqueInTheBatch()
    {
        var a = Photo("IMG_1.jpg");
        var b = Photo("IMG_1.png");
        // Same folder, no suffix: IMG_1.jpg would be the source itself.
        var plan = BatchExport.PlanDestinations([a, b], new BatchExportOptions(_dir, new ExportOptions()));
        Assert.Equal(Path.Combine(_dir, "IMG_1 (2).jpg"), plan[0].Destination);
        Assert.Equal(Path.Combine(_dir, "IMG_1 (3).jpg"), plan[1].Destination);
    }

    [Fact]
    public void Plan_ExistingFiles_ReplaceKeepBothOrSkip()
    {
        var a = Photo("A.jpg");
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        File.WriteAllText(Path.Combine(outDir, "A-edited.jpg"), "old");
        BatchExportOptions Options(ExistingFile e) => new(outDir, new ExportOptions(), "-edited", e);
        Assert.Equal(Path.Combine(outDir, "A-edited.jpg"), BatchExport.PlanDestinations([a], Options(ExistingFile.Replace))[0].Destination);
        Assert.Equal(Path.Combine(outDir, "A-edited (2).jpg"), BatchExport.PlanDestinations([a], Options(ExistingFile.KeepBoth))[0].Destination);
        Assert.Null(BatchExport.PlanDestinations([a], Options(ExistingFile.Skip))[0].Destination);
    }

    [Fact]
    public void ExportOne_AppliesTheSavedEdit_Crop_AndResize()
    {
        var photo = Photo("B.jpg", gray: 60, width: 800, height: 600);
        EditStore.Save(photo, new EditState
        {
            Adjustments = new AdjustmentSettings { Exposure = 1.5 },
            Crop = Crop.None with { Right = 0.5 },
        }, new ImageGeometry(800, 600));
        var dest = Path.Combine(_dir, "out", "B-edited.jpg");
        using var pipeline = Pipeline();
        BatchExport.ExportOne(photo, dest, new ExportOptions(ExportFormat.Jpeg, 90, LongEdge: 300), pipeline);

        using var exported = SKBitmap.Decode(dest);
        Assert.Equal((200, 300), (exported.Width, exported.Height)); // 400 × 600 crop, long edge 300
        Assert.True(exported.GetPixel(100, 150).Red > 90, $"{exported.GetPixel(100, 150)}");
        Assert.True(File.Exists(photo)); // the original is untouched
    }

    [Fact]
    public void ExportOne_UsesTheCachedAiDenoise()
    {
        var photo = Photo("C.jpg", gray: 0, width: 64, height: 48);
        EditStore.Save(photo, new EditState { Adjustments = new AdjustmentSettings { DenoiseAmount = 100 } }, new ImageGeometry(64, 48));
        var cache = new RestoreCache(Path.Combine(_dir, "cache"));
        using (var denoised = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            denoised.Erase(new SKColor(200, 200, 200));
            cache.Save(photo, "denoise", denoised);
        }
        var dest = Path.Combine(_dir, "C-out.png");
        using var pipeline = Pipeline();
        BatchExport.ExportOne(photo, dest, new ExportOptions(ExportFormat.Png), pipeline);
        using var exported = SKBitmap.Decode(dest);
        Assert.InRange(exported.GetPixel(10, 10).Red, 195, 205);
    }
}
