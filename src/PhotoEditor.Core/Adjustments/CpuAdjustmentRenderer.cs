using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// CPU implementation of the adjustment pipeline, used for export and tests.
/// Must match <see cref="AdjustmentShader"/>.
/// </summary>
public static class CpuAdjustmentRenderer
{
    /// <summary>Returns a new RGBA8888 premultiplied bitmap with <paramref name="settings"/> applied.</summary>
    public static SKBitmap Render(SKBitmap source, AdjustmentSettings settings)
    {
        using var src = source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Premul
            ? null
            : source.Copy(SKColorType.Rgba8888);
        var input = src ?? source;
        if (input.AlphaType != SKAlphaType.Premul && input.AlphaType != SKAlphaType.Opaque)
            throw new NotSupportedException($"Unsupported alpha type {input.AlphaType}");

        var result = new SKBitmap(new SKImageInfo(input.Width, input.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var p = PreparedAdjustments.From(settings);
        int width = input.Width;
        int rowBytesIn = input.RowBytes, rowBytesOut = result.RowBytes;
        nint inPtr = input.GetPixels(), outPtr = result.GetPixels();

        Parallel.For(0, input.Height, y =>
        {
            unsafe
            {
                var inRow = new ReadOnlySpan<byte>((byte*)inPtr + (long)y * rowBytesIn, width * 4);
                var outRow = new Span<byte>((byte*)outPtr + (long)y * rowBytesOut, width * 4);
                ProcessRow(inRow, outRow, p);
            }
        });
        return result;
    }

    /// <summary>Processes one row of premultiplied RGBA8888 pixels.</summary>
    public static void ProcessRow(ReadOnlySpan<byte> input, Span<byte> output, in PreparedAdjustments p)
    {
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

            float a = a8 / 255f;
            output[i] = ColorMath.ToByte(ColorMath.LinearToSrgb(Math.Clamp(r, 0f, 1f)) * a);
            output[i + 1] = ColorMath.ToByte(ColorMath.LinearToSrgb(Math.Clamp(g, 0f, 1f)) * a);
            output[i + 2] = ColorMath.ToByte(ColorMath.LinearToSrgb(Math.Clamp(b, 0f, 1f)) * a);
            output[i + 3] = a8;
        }
    }

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
    }
}
