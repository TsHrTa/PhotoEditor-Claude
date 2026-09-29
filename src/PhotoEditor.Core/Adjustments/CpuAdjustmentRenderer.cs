using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// CPU implementation of the adjustment pipeline, used for export and tests.
/// Must match <see cref="AdjustmentShader"/>.
/// </summary>
public static class CpuAdjustmentRenderer
{
    /// <summary>Returns a new RGBA8888 premultiplied bitmap with <paramref name="settings"/> applied.</summary>
    public static SKBitmap Render(SKBitmap source, AdjustmentSettings settings) =>
        Render(source, new EditState { Adjustments = settings });

    /// <summary>
    /// Returns a new RGBA8888 premultiplied bitmap (same size as <paramref name="source"/>) with the global
    /// adjustments and all masks applied. The crop is not applied (see <see cref="ApplyCrop"/>), but the
    /// vignette follows the crop frame.
    /// </summary>
    public static SKBitmap Render(SKBitmap source, EditState state)
    {
        using var src = source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Premul
            ? null
            : source.Copy(SKColorType.Rgba8888);
        var input = src ?? source;
        if (input.AlphaType != SKAlphaType.Premul && input.AlphaType != SKAlphaType.Opaque)
            throw new NotSupportedException($"Unsupported alpha type {input.AlphaType}");
        var p = PreparedAdjustments.From(state.Adjustments);
        // Sharpening works on the source pixels, before everything else (as the shader's global pass does).
        using var sharpened = p.HasSharpening ? Sharpening.Apply(input, p) : null;
        input = sharpened ?? input;

        int width = input.Width, height = input.Height;
        var layers = state.Masks
            .Where(m => m.IsActive)
            .Select(m => new MaskLayer(PreparedAdjustments.From(m.Adjustments), MaskRasterizer.RasterizeToBytes(m, width, height)))
            .ToArray();
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var frame = VignetteMath.Frame.From(state.Crop.Frame(width, height));
        int rowBytesIn = input.RowBytes, rowBytesOut = result.RowBytes;
        nint inPtr = input.GetPixels(), outPtr = result.GetPixels();

        Parallel.For(0, height, y =>
        {
            unsafe
            {
                var inRow = new ReadOnlySpan<byte>((byte*)inPtr + (long)y * rowBytesIn, width * 4);
                var outRow = new Span<byte>((byte*)outPtr + (long)y * rowBytesOut, width * 4);
                ProcessRow(inRow, outRow, p, layers, y, frame);
            }
        });
        return result;
    }

    /// <summary>A mask's prepared adjustments and its 8-bit coverage at output resolution.</summary>
    private sealed record MaskLayer(PreparedAdjustments Adjustments, byte[] Mask);

    /// <summary>Processes one row of premultiplied RGBA8888 pixels.</summary>
    public static void ProcessRow(ReadOnlySpan<byte> input, Span<byte> output, in PreparedAdjustments p) =>
        ProcessRow(input, output, p, [], 0, VignetteMath.Frame.Full(input.Length / 4, 1));

    private static void ProcessRow(ReadOnlySpan<byte> input, Span<byte> output, in PreparedAdjustments p, MaskLayer[] layers, int y, in VignetteMath.Frame frame)
    {
        int width = input.Length / 4;
        for (int i = 0; i < input.Length; i += 4)
        {
            byte a8 = input[i + 3];
            if (a8 == 0)
            {
                output.Slice(i, 4).Clear();
                continue;
            }

            float r, g, b;
            if (a8 == 255)
            {
                r = ColorMath.SrgbByteToLinear(input[i]);
                g = ColorMath.SrgbByteToLinear(input[i + 1]);
                b = ColorMath.SrgbByteToLinear(input[i + 2]);
            }
            else
            {
                float inv = 255f / a8;
                r = ColorMath.SrgbToLinear(Math.Min(1f, input[i] * inv / 255f));
                g = ColorMath.SrgbToLinear(Math.Min(1f, input[i + 1] * inv / 255f));
                b = ColorMath.SrgbToLinear(Math.Min(1f, input[i + 2] * inv / 255f));
            }

            ApplyLinear(ref r, ref g, ref b, p);
            if (p.HasVignette)
                VignetteMath.Apply(ref r, ref g, ref b, i / 4, y, frame, p);
            // Result of the global pass as unpremultiplied sRGB, like the shader hands to the next pass.
            float sr = ColorMath.LinearToSrgb(Math.Clamp(r, 0f, 1f));
            float sg = ColorMath.LinearToSrgb(Math.Clamp(g, 0f, 1f));
            float sb = ColorMath.LinearToSrgb(Math.Clamp(b, 0f, 1f));

            foreach (var layer in layers)
            {
                float m = layer.Mask[y * width + i / 4] / 255f;
                if (m <= 0f)
                    continue;
                r = ColorMath.SrgbToLinear(sr);
                g = ColorMath.SrgbToLinear(sg);
                b = ColorMath.SrgbToLinear(sb);
                ApplyLinear(ref r, ref g, ref b, layer.Adjustments);
                if (layer.Adjustments.HasVignette)
                    VignetteMath.Apply(ref r, ref g, ref b, i / 4, y, frame, layer.Adjustments);
                sr = Mix(sr, ColorMath.LinearToSrgb(Math.Clamp(r, 0f, 1f)), m);
                sg = Mix(sg, ColorMath.LinearToSrgb(Math.Clamp(g, 0f, 1f)), m);
                sb = Mix(sb, ColorMath.LinearToSrgb(Math.Clamp(b, 0f, 1f)), m);
            }

            float a = a8 / 255f;
            output[i] = ColorMath.ToByte(sr * a);
            output[i + 1] = ColorMath.ToByte(sg * a);
            output[i + 2] = ColorMath.ToByte(sb * a);
            output[i + 3] = a8;
        }
    }

    /// <summary>Same formula as SkSL <c>mix</c>.</summary>
    private static float Mix(float x, float y, float t) => x * (1f - t) + y * t;

    /// <summary>The per-pixel adjustment math on linear-light RGB.</summary>
    public static void ApplyLinear(ref float r, ref float g, ref float b, in PreparedAdjustments p)
    {
        r *= p.WhiteBalanceR * p.ExposureGain;
        g *= p.WhiteBalanceG * p.ExposureGain;
        b *= p.WhiteBalanceB * p.ExposureGain;

        // Tone: curve on perceptual luminance, applied to RGB as a ratio (keeps hue).
        float y = ToneCurve.Luminance(r, g, b);
        float yp = MathF.Pow(MathF.Max(y, 0f), 1f / ToneCurve.PerceptualGamma);
        float yp2 = ToneCurve.Apply(yp, p);
        float y2 = MathF.Pow(yp2, ToneCurve.PerceptualGamma);
        if (y > 1e-6f)
        {
            float ratio = y2 / y;
            r *= ratio;
            g *= ratio;
            b *= ratio;
        }
        else
        {
            r = g = b = y2;
        }

        // Vibrance (weighted by how muted the colour is), then saturation, around luminance.
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float sat = max > 1e-6f ? (max - min) / max : 0f;
        float factor = (1f + p.VibranceAmount * (1f - sat)) * p.SaturationFactor;
        y = ToneCurve.Luminance(r, g, b);
        r = MathF.Max(y + (r - y) * factor, 0f);
        g = MathF.Max(y + (g - y) * factor, 0f);
        b = MathF.Max(y + (b - y) * factor, 0f);

        if (p.HasHsl)
            HslMath.Apply(ref r, ref g, ref b, p.Hsl);
    }

    /// <summary>
    /// Cuts the (rotated) crop out of <paramref name="source"/> with bilinear sampling; returns the input
    /// itself when <paramref name="crop"/> is the whole image.
    /// </summary>
    public static SKBitmap ApplyCrop(SKBitmap source, Crop crop)
    {
        if (crop.IsDefault)
            return source;
        var f = crop.Frame(source.Width, source.Height);
        var (w, h) = crop.OutputSize(source.Width, source.Height);
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        using var image = SKImage.FromBitmap(source);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees((float)-f.Angle);
        canvas.Translate((float)-f.CenterX, (float)-f.CenterY);
        // Clamp at the image border so edge pixels are not blended with transparency.
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(-source.Width, -source.Height, 3f * source.Width, 3f * source.Height, paint);
        return result;
    }
}
