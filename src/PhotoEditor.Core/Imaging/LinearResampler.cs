using PhotoEditor.Core.Adjustments;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Downscaling of a photo as light behaves: the 8-bit values (plus the RAW's <see cref="Headroom"/> layers, if it
/// has them) are decoded to linear light, filtered with a separable Lanczos-3 (a proper low-pass at the new size,
/// not a 2 × 2 box and a bilinear step), and encoded again — averaging gamma-encoded values darkens fine bright
/// detail (thin branches against the sky, specular highlights, stars). The result is split into the 8-bit photo and
/// layers again, so values above white and the fraction of a step survive in the smaller image.
/// </summary>
public static class LinearResampler
{
    /// <summary>
    /// Resizes an opaque RGBA8888 photo (with its layers) to <paramref name="width"/> × <paramref name="height"/>.
    /// Returns null when the input is not such a photo (another colour type, transparent pixels, or not a
    /// reduction): the caller then falls back to <see cref="PreviewImage.Downscale"/>.
    /// </summary>
    public static (SKBitmap Photo, Headroom? Layers)? TryResize(SKBitmap source, Headroom? layers, int width, int height)
    {
        if (source.ColorType != SKColorType.Rgba8888 || width > source.Width || height > source.Height || width < 1 || height < 1)
            return null;
        if (layers is not null && (layers.Width != source.Width || layers.Height != source.Height))
            layers = null;

        var photo = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var extra = layers is null ? null : new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var fine = layers?.Fine is null ? null : new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));

        var horizontal = Kernel.Build(source.Width, width);
        var vertical = Kernel.Build(source.Height, height);
        bool opaque = true;
        const int band = 32;
        int bands = (height + band - 1) / band;
        nint srcPtr = source.GetPixels(), photoPtr = photo.GetPixels();
        nint extraPtr = extra?.GetPixels() ?? 0, finePtr = fine?.GetPixels() ?? 0;
        nint layerPtr = layers?.Bitmap.GetPixels() ?? 0, layerFinePtr = layers?.Fine?.GetPixels() ?? 0;
        int srcRow = source.RowBytes, photoRow = photo.RowBytes, extraRow = extra?.RowBytes ?? 0, fineRow = fine?.RowBytes ?? 0;
        int layerRow = layers?.Bitmap.RowBytes ?? 0, layerFineRow = layers?.Fine?.RowBytes ?? 0;
        float layerScale = layers?.Scale ?? 0;

        Parallel.For(0, bands, b =>
        {
            int y0 = b * band, y1 = Math.Min(height, y0 + band);
            int s0 = vertical.Start[y0], s1 = vertical.Start[y1 - 1] + vertical.Count[y1 - 1];
            for (int y = y0; y < y1; y++)
                s0 = Math.Min(s0, vertical.Start[y]);
            for (int y = y0; y < y1; y++)
                s1 = Math.Max(s1, vertical.Start[y] + vertical.Count[y]);
            // The source rows s0..s1 filtered horizontally, in linear light.
            var rows = new float[(s1 - s0) * width * 3];
            var decoded = new float[source.Width * 3];
            bool opaqueHere = true;
            unsafe
            {
                for (int sy = s0; sy < s1; sy++)
                {
                    byte* p = (byte*)srcPtr + (long)sy * srcRow;
                    byte* h = layers is null ? null : (byte*)layerPtr + (long)sy * layerRow;
                    byte* f = layerFinePtr == 0 ? null : (byte*)layerFinePtr + (long)sy * layerFineRow;
                    for (int x = 0; x < source.Width; x++)
                    {
                        if (p[x * 4 + 3] != 255)
                            opaqueHere = false;
                        for (int c = 0; c < 3; c++)
                            decoded[x * 3 + c] = Decode(p[x * 4 + c], h is null ? (byte)0 : h[x * 4 + c], f is null ? Headroom.FineZero : f[x * 4 + c], layerScale);
                    }
                    int rowBase = (sy - s0) * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        int start = horizontal.Start[x], count = horizontal.Count[x];
                        int k = x * horizontal.Stride;
                        float r = 0, g = 0, bl = 0;
                        for (int i = 0; i < count; i++)
                        {
                            float w = horizontal.Weights[k + i];
                            int at = (start + i) * 3;
                            r += w * decoded[at];
                            g += w * decoded[at + 1];
                            bl += w * decoded[at + 2];
                        }
                        rows[rowBase + x * 3] = r;
                        rows[rowBase + x * 3 + 1] = g;
                        rows[rowBase + x * 3 + 2] = bl;
                    }
                }
                if (!opaqueHere)
                    opaque = false;
                for (int y = y0; y < y1; y++)
                {
                    byte* po = (byte*)photoPtr + (long)y * photoRow;
                    byte* eo = extra is null ? null : (byte*)extraPtr + (long)y * extraRow;
                    byte* fo = fine is null ? null : (byte*)finePtr + (long)y * fineRow;
                    int start = vertical.Start[y], count = vertical.Count[y], k = y * vertical.Stride;
                    for (int x = 0; x < width; x++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float v = 0;
                            for (int i = 0; i < count; i++)
                                v += vertical.Weights[k + i] * rows[((start + i - s0) * width + x) * 3 + c];
                            Encode(MathF.Max(v, 0f), layerScale, po + x * 4 + c,
                                eo is null ? null : eo + x * 4 + c, fo is null ? null : fo + x * 4 + c);
                        }
                        po[x * 4 + 3] = 255;
                        if (eo is not null) eo[x * 4 + 3] = 255;
                        if (fo is not null) fo[x * 4 + 3] = 255;
                    }
                }
            }
        });

        if (!opaque)
        {
            photo.Dispose();
            extra?.Dispose();
            fine?.Dispose();
            return null;
        }
        var resized = extra is null ? null : new Headroom(extra, layerScale, fine) { BaseCurve = layers!.BaseCurve };
        return (photo, resized);
    }

    /// <summary>A channel as linear light: the 8-bit value, the part above white and the rounded-away fraction.</summary>
    private static float Decode(byte photo, byte above, byte fraction, float scale)
    {
        if (above != 0 && scale > 0)
            return ColorMath.SrgbToLinear(1f + scale * above / 255f);
        return fraction == Headroom.FineZero
            ? ColorMath.SrgbByteToLinear(photo)
            : ColorMath.SrgbByteToLinear(photo, Headroom.FineOffset(fraction));
    }

    private static unsafe void Encode(float linear, float scale, byte* photo, byte* extra, byte* fine)
    {
        float e = ColorMath.LinearToSrgbFast(linear);
        if (e >= 1f)
        {
            *photo = 255;
            if (extra is not null)
                *extra = scale > 0 ? (byte)Math.Clamp(MathF.Round((e - 1f) / scale * 255f), 0f, 255f) : (byte)0;
            if (fine is not null)
                *fine = Headroom.FineZero;
            return;
        }
        byte b = (byte)MathF.Round(e * 255f);
        *photo = b;
        if (extra is not null)
            *extra = 0;
        if (fine is not null)
            *fine = (byte)Math.Clamp(MathF.Round(Headroom.FineZero + (e - b / 255f) / Headroom.FineStep), 0f, 255f);
    }

    /// <summary>Lanczos-3 weights of every output position along one axis (normalised, edges clamped).</summary>
    private sealed class Kernel
    {
        public int[] Start = [];
        public int[] Count = [];
        public float[] Weights = [];
        public int Stride;

        public static Kernel Build(int sourceSize, int targetSize)
        {
            double scale = (double)sourceSize / targetSize;
            double filterScale = Math.Max(1.0, scale), support = 3.0 * filterScale;
            var kernel = new Kernel { Stride = (int)Math.Ceiling(support * 2) + 2 };
            kernel.Start = new int[targetSize];
            kernel.Count = new int[targetSize];
            kernel.Weights = new float[targetSize * kernel.Stride];
            for (int i = 0; i < targetSize; i++)
            {
                double center = (i + 0.5) * scale - 0.5;
                int lo = Math.Max(0, (int)Math.Ceiling(center - support));
                int hi = Math.Min(sourceSize - 1, (int)Math.Floor(center + support));
                double sum = 0;
                for (int j = lo; j <= hi; j++)
                    sum += Lanczos((j - center) / filterScale);
                kernel.Start[i] = lo;
                kernel.Count[i] = hi - lo + 1;
                for (int j = lo; j <= hi; j++)
                    kernel.Weights[i * kernel.Stride + j - lo] = (float)(Lanczos((j - center) / filterScale) / sum);
            }
            return kernel;
        }

        private static double Lanczos(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-9)
                return 1;
            if (x >= 3)
                return 0;
            double px = Math.PI * x;
            return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }
    }
}
