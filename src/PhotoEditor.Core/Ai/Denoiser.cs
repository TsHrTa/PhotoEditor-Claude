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
/// AI noise reduction with SCUNet (real-photo variant): the photo is processed in overlapping tiles
/// (512 × 512 windows; only the centre of each is kept, so no seams) because full photos do not fit the model.
/// Input / output are RGB 0..1, sizes divisible by 8.
/// </summary>
public sealed class Denoiser : IDisposable
{
    /// <summary>Model input window (divisible by 8).</summary>
    public const int Window = 512;

    /// <summary>Context kept around each tile's written core.</summary>
    public const int Margin = 32;

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
    /// Runs <paramref name="run"/> (1 × 3 × H × W RGB 0..1 → same shape) over the image in overlapping tiles
    /// and assembles the result. Windows smaller than <see cref="Window"/> (small images) are padded by
    /// mirroring to a multiple of 8.
    /// </summary>
    public static SKBitmap ProcessTiles(SKBitmap image, Func<Tensor, Tensor> run, IProgress<TileProgress>? progress = null,
        CancellationToken cancel = default)
    {
        using var converted = image.ColorType == SKColorType.Rgba8888 && image.AlphaType == SKAlphaType.Premul
            ? null
            : image.Copy(SKColorType.Rgba8888);
        var source = converted ?? image;
        int width = source.Width, height = source.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var src = source.GetPixelSpan().ToArray();
        int srcRow = source.RowBytes, dstRow = result.RowBytes;
        var dst = new byte[dstRow * height];

        const int core = Window - 2 * Margin;
        int tilesX = (width + core - 1) / core, tilesY = (height + core - 1) / core, total = tilesX * tilesY, done = 0;
        int winW = Math.Min(Window, RoundUp8(width)), winH = Math.Min(Window, RoundUp8(height));
        var input = new float[3 * winW * winH];
        int plane = winW * winH;

        for (int ty = 0; ty < tilesY; ty++)
        for (int tx = 0; tx < tilesX; tx++)
        {
            cancel.ThrowIfCancellationRequested();
            int cx0 = tx * core, cy0 = ty * core;
            int cx1 = Math.Min(width, cx0 + core), cy1 = Math.Min(height, cy0 + core);
            // Window around the core, shifted inside the image where possible.
            int wx = Math.Clamp(cx0 - Margin, 0, Math.Max(0, width - winW));
            int wy = Math.Clamp(cy0 - Margin, 0, Math.Max(0, height - winH));
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
            for (int y = cy0; y < cy1; y++)
            for (int x = cx0; x < cx1; x++)
            {
                int i = (y - wy) * winW + (x - wx), o = y * srcRow + x * 4, d = y * dstRow + x * 4;
                float a = src[o + 3] / 255f;
                dst[d] = ToByte(output[i] * a);
                dst[d + 1] = ToByte(output[plane + i] * a);
                dst[d + 2] = ToByte(output[2 * plane + i] * a);
                dst[d + 3] = src[o + 3];
            }
            progress?.Report(new TileProgress(++done, total));
        }
        System.Runtime.InteropServices.Marshal.Copy(dst, 0, result.GetPixels(), dst.Length);
        return result;
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

    /// <summary>Cache file for the photo at <paramref name="imagePath"/> (changes when the photo file changes).</summary>
    public string PathFor(string imagePath)
    {
        var info = new FileInfo(imagePath);
        var key = $"{Path.GetFullPath(imagePath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ModelCatalog.DenoiseGraph.Sha256}";
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
