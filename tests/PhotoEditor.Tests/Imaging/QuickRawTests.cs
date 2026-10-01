using System.Diagnostics;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotoEditor.Tests.Imaging;

public sealed class QuickRawTests(ITestOutputHelper output)
{
    private static string? Sample()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PhotoEditor.slnx")))
            dir = Path.GetDirectoryName(dir);
        var path = dir is null ? null : Path.Combine(dir, "samples", "IMG_5645.CR3");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static double MeanLinear(SKBitmap b)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < b.Height; y += 8)
            for (int x = 0; x < b.Width; x += 8)
            {
                var c = b.GetPixel(x, y);
                sum += 0.2126 * ColorMath.SrgbToLinear(c.Red / 255f) + 0.7152 * ColorMath.SrgbToLinear(c.Green / 255f) + 0.0722 * ColorMath.SrgbToLinear(c.Blue / 255f);
                n++;
            }
        return sum / n;
    }

    [Fact]
    public void TheQuickRender_IsHalfSizeAndHasTheToneOfTheFullDecode()
    {
        var path = Sample();
        if (path is null)
            return;
        var watch = Stopwatch.StartNew();
        using var quick = RawImageLoader.LoadQuick(path);
        var quickTime = watch.Elapsed;
        Assert.NotNull(quick);
        watch.Restart();
        using var full = RawImageLoader.Load(path);
        output.WriteLine($"quick {quick!.Width} x {quick.Height} in {quickTime.TotalSeconds:0.00} s, full {full.Width} x {full.Height} in {watch.Elapsed.TotalSeconds:0.00} s");
        Assert.InRange(quick.Width, full.Width / 2 - 2, full.Width / 2 + 2);
        Assert.InRange(quick.Height, full.Height / 2 - 2, full.Height / 2 + 2);
        Assert.True(Headroom.Of(quick)?.BaseCurve == true);
        // The same rendering: the average light of the two agrees within 3 %, a camera JPEG's tone curve would be 25 % off.
        double ratio = MeanLinear(quick) / MeanLinear(full);
        Assert.InRange(ratio, 0.97, 1.03);
    }
}
