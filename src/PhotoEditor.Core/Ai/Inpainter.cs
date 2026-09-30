using PhotoEditor.Core.Retouch;
using SkiaSharp;

namespace PhotoEditor.Core.Ai;

/// <summary>
/// AI Remove: fills holes in a photo with the LaMa inpainting model (512 × 512). The fill of a Remove spot is
/// computed once from the photo around it and kept in the <see cref="FillStore"/>.
/// </summary>
public sealed class Inpainter(OnnxModel model) : IDisposable
{
    public InferenceDevice Device => model.Device;
    public string? FallbackReason => model.FallbackReason;

    public static Inpainter Load(string modelPath) => new(OnnxModel.Load(modelPath));

    /// <summary>Fills <paramref name="hole"/> (true = fill) in a <see cref="RemoveFill.ModelSize"/>² image.</summary>
    public SKBitmap Fill(SKBitmap image, bool[] hole)
    {
        const int n = RemoveFill.ModelSize;
        if (image.Width != n || image.Height != n || hole.Length != n * n)
            throw new ArgumentException($"The inpainting model takes {n} × {n} pixels.");
        var input = new float[3 * n * n];
        var mask = new float[n * n];
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < n * n; i++)
        {
            float a = pixels[i * 4 + 3];
            float inv = a > 0 ? 1f / a : 0f;
            // unpremultiplied RGB 0..1, planar; the hole itself is blanked (the model ignores it anyway)
            for (int c = 0; c < 3; c++)
                input[c * n * n + i] = hole[i] ? 0f : pixels[i * 4 + c] * inv;
            mask[i] = hole[i] ? 1f : 0f;
        }
        var outputs = model.Run(new Dictionary<string, Tensor>
        {
            [model.InputNames[0]] = new(input, [1, 3, n, n]),
            [model.InputNames[1]] = new(mask, [1, 1, n, n]),
        });
        var output = outputs.Values.First().Data;
        var result = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgba8888, SKAlphaType.Premul));
        var dst = result.GetPixelSpan();
        for (int i = 0; i < n * n; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                // outside the hole the photo itself (the model may shift it slightly)
                float value = hole[i] ? output[c * n * n + i] : pixels[i * 4 + c] * (255f / Math.Max((int)pixels[i * 4 + 3], 1));
                dst[i * 4 + c] = (byte)Math.Clamp(MathF.Round(value), 0, 255);
            }
            dst[i * 4 + 3] = 255;
        }
        return result;
    }

    /// <summary>Computes the fill for a Remove spot on <paramref name="photo"/> (full size) and returns its id in the <see cref="FillStore"/>.</summary>
    public string FillSpot(SKBitmap photo, Spot spot)
    {
        var (image, hole) = RemoveFill.ModelInput(photo, spot);
        using var _ = image;
        using var fill = Fill(image, hole);
        return FillStore.Save(fill);
    }

    public void Dispose() => model.Dispose();
}
