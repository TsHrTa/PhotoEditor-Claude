using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>Progress of a tiled AI operation.</summary>
public readonly record struct TileProgress(int Done, int Total)
{
    public double Fraction => Total == 0 ? 1 : (double)Done / Total;
}

/// <summary>
/// AI noise reduction with SCUNet (real-photo variant). Full photos do not fit the model, so it runs on
/// overlapping 512 × 512 windows; neighbouring results are cross-faded over the overlap (each window's output
/// drifts slightly in brightness, which a hard cut shows as bands in flat dark areas). Rows of windows are
/// accumulated in a band buffer, so memory stays small even for 24 MP photos.
/// Input / output are RGB 0..1, sizes divisible by 8.
/// </summary>
public sealed class Denoiser : IDisposable
{
    /// <summary>Model input window (divisible by 8).</summary>
    public const int Window = 512;

    /// <summary>Overlap between neighbouring windows (cross-faded).</summary>
    public const int Overlap = 96;

    private readonly OnnxModel _model;

    private Denoiser(OnnxModel model) => _model = model;

    public InferenceDevice Device => _model.Device;
    public string? FallbackReason => _model.FallbackReason;

    public static Denoiser Load(ModelStore store, InferenceDevice preferred = InferenceDevice.DirectML) =>
        new(OnnxModel.Load(store.PathOf(ModelCatalog.DenoiseGraph), preferred));

    /// <summary>Returns the denoised photo (same size, RGBA8888 premultiplied, alpha kept).</summary>
    public SKBitmap Denoise(SKBitmap image, IProgress<TileProgress>? progress = null, CancellationToken cancel = default) =>
        ProcessTiles(image, input => _model.Run(new Dictionary<string, Tensor> { [_model.InputNames[0]] = input })[_model.OutputNames[0]],
            progress, cancel);

    /// <summary>
    /// Runs <paramref name="run"/> (1 × 3 × H × W RGB 0..1 → same shape) over the image in overlapping windows
    /// and assembles the cross-faded result. Windows larger than a small image are mirror-padded to a multiple of 8.
    /// </summary>
    public static SKBitmap ProcessTiles(SKBitmap image, Func<Tensor, Tensor> run, IProgress<TileProgress>? progress = null,
        CancellationToken cancel = default)
    {
        using var converted = image.ColorType == SKColorType.Rgba8888 && image.AlphaType == SKAlphaType.Premul
            ? null
            : image.Copy(SKColorType.Rgba8888);
        var source = converted ?? image;
        int width = source.Width, height = source.Height;
        var src = source.GetPixelSpan().ToArray();
        int srcRow = source.RowBytes;
        var dst = new byte[width * 4 * height];

        int winW = Math.Min(Window, RoundUp8(width)), winH = Math.Min(Window, RoundUp8(height));
        var xs = Starts(width, winW);
        var ys = Starts(height, winH);
        int total = xs.Length * ys.Length, done = 0;
        int plane = winW * winH;
        var input = new float[3 * plane];

        // Band of rows [bandTop, bandTop + winH) with weighted sums (r, g, b, weight).
        var band = new float[winH * width * 4];
        int bandTop = ys[0];
        for (int r = 0; r < ys.Length; r++)
        {
            int wy = ys[r];
            if (wy != bandTop)
            {
                // Rows above wy are final: write them out and move the rest of the band up.
                int shift = wy - bandTop;
                WriteRows(band, width, bandTop, Math.Min(shift, height - bandTop), src, srcRow, dst);
                Array.Copy(band, shift * width * 4, band, 0, (winH - shift) * width * 4);
                Array.Clear(band, (winH - shift) * width * 4, shift * width * 4);
                bandTop = wy;
            }
            for (int c = 0; c < xs.Length; c++)
            {
                cancel.ThrowIfCancellationRequested();
                int wx = xs[c];
                for (int y = 0; y < winH; y++)
                {
                    int sy = Mirror(wy + y, height);
                    for (int x = 0; x < winW; x++)
                    {
                        int sx = Mirror(wx + x, width);
                        int o = sy * srcRow + sx * 4, i = y * winW + x;
                        float a = src[o + 3];
                        float inv = a > 0 ? 1f / a : 0;
                        input[i] = src[o] * inv;
                        input[plane + i] = src[o + 1] * inv;
                        input[2 * plane + i] = src[o + 2] * inv;
                    }
                }
                var output = run(new Tensor(input, [1, 3, winH, winW])).Data;
                bool left = c > 0, right = c < xs.Length - 1, top = r > 0, bottom = r < ys.Length - 1;
                for (int y = 0; y < winH && wy + y < height; y++)
                {
                    float fy = Fade(y, winH, top, bottom);
                    for (int x = 0; x < winW && wx + x < width; x++)
                    {
                        float w = fy * Fade(x, winW, left, right);
                        int i = y * winW + x, b = (y * width + wx + x) * 4;
                        band[b] += w * output[i];
                        band[b + 1] += w * output[plane + i];
                        band[b + 2] += w * output[2 * plane + i];
                        band[b + 3] += w;
                    }
                }
                progress?.Report(new TileProgress(++done, total));
            }
        }
        WriteRows(band, width, bandTop, Math.Min(winH, height - bandTop), src, srcRow, dst);

        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(dst, 0, result.GetPixels(), dst.Length);
        return result;
    }

    /// <summary>Window start positions covering 0..size with at least <see cref="Overlap"/> between neighbours.</summary>
    private static int[] Starts(int size, int window)
    {
        if (size <= window)
            return [0];
        int stride = window - Overlap;
        int count = (size - window + stride - 1) / stride + 1;
        // Spread the windows evenly so the last one ends exactly at the image edge.
        return Enumerable.Range(0, count).Select(i => (int)Math.Round((double)i * (size - window) / (count - 1))).ToArray();
    }

    /// <summary>Cross-fade weight along one axis: ramps up over the overlap where a neighbour exists.</summary>
    private static float Fade(int position, int window, bool before, bool after)
    {
        float w = 1f;
        if (before && position < Overlap)
            w = Math.Min(w, (position + 0.5f) / Overlap);
        if (after && position >= window - Overlap)
            w = Math.Min(w, (window - position - 0.5f) / Overlap);
        return w;
    }

    private static void WriteRows(float[] band, int width, int top, int rows, byte[] src, int srcRow, byte[] dst)
    {
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < width; x++)
        {
            int b = (y * width + x) * 4, o = (top + y) * srcRow + x * 4, d = ((top + y) * width + x) * 4;
            float wsum = band[b + 3];
            float a = src[o + 3] / 255f;
            if (wsum <= 0)
            {
                dst[d] = src[o]; dst[d + 1] = src[o + 1]; dst[d + 2] = src[o + 2];
            }
            else
            {
                dst[d] = ToByte(band[b] / wsum * a);
                dst[d + 1] = ToByte(band[b + 1] / wsum * a);
                dst[d + 2] = ToByte(band[b + 2] / wsum * a);
            }
            dst[d + 3] = src[o + 3];
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private static int RoundUp8(int v) => (v + 7) / 8 * 8;

    /// <summary>Mirror-pads coordinates outside 0..size-1.</summary>
    private static int Mirror(int v, int size)
    {
        if (size == 1)
            return 0;
        int period = 2 * (size - 1);
        v = ((v % period) + period) % period;
        return v < size ? v : period - v;
    }

    /// <summary>Linear blend of two same-size RGBA8888 bitmaps: a + (b − a) × <paramref name="t"/>.</summary>
    public static SKBitmap Blend(SKBitmap a, SKBitmap b, double t)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException("Images must have the same size.");
        var result = new SKBitmap(new SKImageInfo(a.Width, a.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        var pr = result.GetPixelSpan();
        int w = (int)Math.Round(Math.Clamp(t, 0, 1) * 256);
        for (int i = 0; i < pr.Length; i++)
            pr[i] = (byte)((pa[i] * (256 - w) + pb[i] * w + 128) >> 8);
        return result;
    }

    public void Dispose() => _model.Dispose();
}

/// <summary>
/// Keeps denoised photos on disk (<c>%LOCALAPPDATA%\PhotoEditor\cache\denoise</c>), keyed by the photo's path,
/// size and modification time, so the slow AI step runs once per photo.
/// </summary>
public sealed class DenoiseCache(string directory)
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoEditor", "cache", "denoise");

    public string Directory { get; } = directory;

    /// <summary>Bump when the denoise processing changes, so old results are recomputed.</summary>
    public const int Version = 2;

    /// <summary>Cache file for the photo at <paramref name="imagePath"/> (changes when the photo file changes).</summary>
    public string PathFor(string imagePath)
    {
        var info = new FileInfo(imagePath);
        var key = $"{Path.GetFullPath(imagePath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ModelCatalog.DenoiseGraph.Sha256}|v{Version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Directory, hash + ".png");
    }

    public SKBitmap? Load(string imagePath, int width, int height)
    {
        var path = PathFor(imagePath);
        if (!File.Exists(path))
            return null;
        using var decoded = SKBitmap.Decode(path);
        if (decoded is null || decoded.Width != width || decoded.Height != height)
            return null;
        return decoded.Copy(SKColorType.Rgba8888);
    }

    public void Save(string imagePath, SKBitmap denoised)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(imagePath);
        var temp = path + ".tmp";
        using (var data = denoised.Encode(SKEncodedImageFormat.Png, 1) ?? throw new InvalidOperationException("Could not encode."))
        using (var file = File.Create(temp))
            data.SaveTo(file);
        File.Move(temp, path, overwrite: true);
    }
}
