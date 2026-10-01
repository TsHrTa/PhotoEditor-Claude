using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.Core.Library;
using SkiaSharp;

namespace PhotoEditor.Tests.Library;

public sealed class PhotoFolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("folder-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private string Photo(string name, SKColor color, int width = 400, int height = 300)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(color);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 95);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void List_SortsByName_HidesJpegOfRawPairs_SkipsOtherFiles()
    {
        foreach (var name in new[] { "IMG_0002.CR3", "IMG_0002.JPG", "IMG_0001.jpg", "IMG_0010.cr3", "notes.txt",
                     "IMG_0001.jpg.json", "IMG_0003.png", ".hidden.jpg" })
            Touch(name);
        var names = PhotoFolder.List(_dir).Select(Path.GetFileName).ToList();
        Assert.Equal(["IMG_0001.jpg", "IMG_0002.CR3", "IMG_0003.png", "IMG_0010.cr3"], names);

        var all = PhotoFolder.List(_dir, hideJpegWithRaw: false).Select(Path.GetFileName).ToList();
        Assert.Equal(["IMG_0001.jpg", "IMG_0002.CR3", "IMG_0002.JPG", "IMG_0003.png", "IMG_0010.cr3"], all);
    }

    [Fact]
    public void HasEdits_LooksForTheSidecars()
    {
        var photo = Photo("a.jpg", SKColors.Gray);
        Assert.False(PhotoFolder.HasEdits(photo));
        EditStore.Save(photo, new EditState { Adjustments = new AdjustmentSettings { Exposure = 1 } }, new ImageGeometry(400, 300));
        Assert.True(PhotoFolder.HasEdits(photo));
    }

    [Fact]
    public void EditedThumbnail_ShowsTheSavedEdit()
    {
        var cache = new ThumbnailCache(Path.Combine(_dir, "cache"), 100);
        var photo = Photo("b.jpg", new SKColor(80, 80, 80));
        using (var plain = PhotoFolder.EditedThumbnail(cache, photo)!)
            Assert.Equal((100, 75), (plain.Width, plain.Height));

        var edit = new EditState
        {
            Adjustments = new AdjustmentSettings { Exposure = 2 },
            Crop = Crop.None with { Left = 0, Top = 0, Right = 0.5, Bottom = 1 },
        };
        EditStore.Save(photo, edit, new ImageGeometry(400, 300));
        using var edited = PhotoFolder.EditedThumbnail(cache, photo)!;
        Assert.Equal((50, 75), (edited.Width, edited.Height));
        Assert.True(edited.GetPixel(25, 37).Red > 150, $"{edited.GetPixel(25, 37)}");
    }

    [Fact]
    public void EditedThumbnail_IgnoresABrokenSidecar()
    {
        var cache = new ThumbnailCache(Path.Combine(_dir, "cache"), 100);
        var photo = Photo("c.jpg", SKColors.Gray);
        File.WriteAllText(SidecarFile.PathFor(photo), "{ not json");
        using var thumb = PhotoFolder.EditedThumbnail(cache, photo);
        Assert.NotNull(thumb);
    }
}
