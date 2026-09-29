using SkiaSharp;

namespace PhotoEditor.Tests;

internal static class TestImages
{
    /// <summary>Deterministic image with a grey ramp, saturated colours, random pixels and some transparency.</summary>
    public static SKBitmap Varied(int width = 64, int height = 64, bool withAlpha = true)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rnd = new Random(1234);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            SKColor c = y switch
            {
                < 8 => new SKColor((byte)(x * 255 / (width - 1)), (byte)(x * 255 / (width - 1)), (byte)(x * 255 / (width - 1))),
                < 16 => SKColor.FromHsv(x * 360f / width, 100, 100),
                < 24 => SKColor.FromHsv(x * 360f / width, 40, 60),
                _ => new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)),
            };
            if (withAlpha && y >= height - 4)
                c = c.WithAlpha((byte)(x * 255 / (width - 1)));
            bmp.SetPixel(x, y, c);
        }
        return bmp;
    }

    public static SKBitmap Solid(SKColor color, int size = 4)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(color);
        return bmp;
    }

    /// <summary>Largest per-channel difference between two premultiplied bitmaps of the same size.</summary>
    public static int MaxDifference(SKBitmap a, SKBitmap b, out (int X, int Y) where)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int max = 0, at = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            int d = Math.Abs(pa[i] - pb[i]);
            if (d > max) { max = d; at = i; }
        }
        where = ((at / 4) % a.Width, at / 4 / a.Width);
        return max;
    }
}
