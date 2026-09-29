using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thumbs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Photo(string name, int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(color);
        var path = Path.Combine(_dir, name);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void Get_MakesASmallThumbnail_AndReusesTheCachedFile()
    {
        var cache = new ThumbnailCache(Path.Combine(_dir, "cache"), 100);
        var photo = Photo("a.jpg", 1200, 800, SKColors.Orange);
        using (var first = cache.Get(photo)!)
        {
            Assert.Equal((100, 67), (first.Width, first.Height));
            Assert.True(first.GetPixel(50, 30).Red > 200);
        }
        var cached = cache.PathFor(photo);
        Assert.True(File.Exists(cached));

        // A cached thumbnail is used as-is (mark it to prove it).
        using (var marked = new SKBitmap(new SKImageInfo(10, 10)))
        {
            marked.Erase(SKColors.Blue);
            using var data = SKImage.FromBitmap(marked).Encode(SKEncodedImageFormat.Jpeg, 90);
            File.WriteAllBytes(cached, data.ToArray());
        }
        using var second = cache.Get(photo)!;
        Assert.Equal(10, second.Width);
    }

    [Fact]
    public void ChangedPhoto_GetsANewThumbnail()
    {
        var cache = new ThumbnailCache(Path.Combine(_dir, "cache"), 100);
        var photo = Photo("b.jpg", 400, 400, SKColors.Red);
        var before = cache.PathFor(photo);
        Photo("b.jpg", 400, 300, SKColors.Green);
        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(before, cache.PathFor(photo));
        using var thumb = cache.Get(photo)!;
        Assert.Equal((100, 75), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void UnreadablePhoto_GivesNull()
    {
        var cache = new ThumbnailCache(Path.Combine(_dir, "cache"), 100);
        var bad = Path.Combine(_dir, "bad.jpg");
        File.WriteAllBytes(bad, [1, 2, 3]);
        Assert.Null(cache.Get(bad));
        Assert.Null(cache.Get(Path.Combine(_dir, "missing.jpg")));
    }
}
