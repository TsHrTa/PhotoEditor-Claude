using System.Diagnostics;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Bench;

/// <summary>Decode time and a 100 % crop per demosaicing algorithm; runs only when PHOTOEDITOR_BENCH names a RAW file.</summary>
public sealed class DemosaicBench(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        var path = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH");
        var outDir = Environment.GetEnvironmentVariable("PHOTOEDITOR_BENCH_OUT");
        if (path is null || outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        foreach (var algorithm in new[] { RawDemosaic.Ppg, RawDemosaic.Ahd, RawDemosaic.Dcb, RawDemosaic.Dht, RawDemosaic.Aahd })
        {
            var w = Stopwatch.StartNew();
            using var photo = RawImageLoader.Load(path, algorithm);
            output.WriteLine($"{algorithm}: {w.ElapsedMilliseconds} ms ({photo.Width} x {photo.Height})");
            int size = 600, x = photo.Width / 2 - size / 2, y = photo.Height / 3;
            using var crop = new SKBitmap(size, size);
            photo.ExtractSubset(crop, new SKRectI(x, y, x + size, y + size));
            using var image = SKImage.FromBitmap(crop);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(outDir, $"{algorithm}.png"), data.ToArray());
        }
    }
}
