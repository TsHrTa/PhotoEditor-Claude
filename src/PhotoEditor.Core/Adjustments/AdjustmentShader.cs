using SkiaSharp;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// GPU implementation of the adjustment pipeline as an SkSL runtime shader.
/// The math must match <see cref="CpuAdjustmentRenderer"/>; the tests compare both.
/// </summary>
public static class AdjustmentShader
{
    public const string Source = """
        uniform shader image;
        uniform float exposureGain;
        uniform float contrastGamma;
        uniform float highlightsAmount;
        uniform float shadowsAmount;
        uniform float whitesAmount;
        uniform float blacksAmount;

        const float perceptualGamma = 2.2;

        float srgbToLinear(float c) {
            return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
        }
        float linearToSrgb(float c) {
            return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        }

        float contrastCurve(float x, float g) {
            if (x <= 0.0 || x >= 1.0) return x;
            return x < 0.5 ? 0.5 * pow(2.0 * x, g) : 1.0 - 0.5 * pow(2.0 - 2.0 * x, g);
        }

        float toneCurve(float x) {
            x = contrastCurve(x, contrastGamma);
            x += highlightsAmount * smoothstep(0.35, 1.0, x);
            float xs = clamp(x, 0.0, 1.0);
            x += shadowsAmount * 6.75 * xs * (1.0 - xs) * (1.0 - xs);
            xs = clamp(x, 0.0, 1.0);
            x += whitesAmount * xs * xs * xs * xs;
            float inv = 1.0 - xs;
            x += blacksAmount * inv * inv * inv * inv;
            return max(x, 0.0);
        }

        half4 main(float2 coord) {
            half4 src = image.eval(coord);
            float a = src.a;
            if (a <= 0.0) return half4(0);
            float3 c = float3(src.rgb) / a;

            // Linear light
            c = float3(srgbToLinear(c.r), srgbToLinear(c.g), srgbToLinear(c.b));
            c *= exposureGain;

            // Tone curve on perceptual luminance, applied as a ratio
            float y = dot(c, float3(0.2126, 0.7152, 0.0722));
            float y2 = pow(toneCurve(pow(max(y, 0.0), 1.0 / perceptualGamma)), perceptualGamma);
            c = y > 1e-6 ? c * (y2 / y) : float3(y2);

            c = clamp(c, 0.0, 1.0);
            c = float3(linearToSrgb(c.r), linearToSrgb(c.g), linearToSrgb(c.b));
            return half4(half3(c * a), half(a));
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> LazyEffect = new(() =>
        SKRuntimeEffect.CreateShader(Source, out var errors)
            ?? throw new InvalidOperationException("Adjustment shader failed to compile: " + errors));

    public static SKRuntimeEffect Effect => LazyEffect.Value;

    /// <summary>Creates the adjustment shader for <paramref name="image"/> in the image's pixel coordinates.</summary>
    public static SKShader CreateShader(SKImage image, AdjustmentSettings settings, SKSamplingOptions sampling)
    {
        var effect = Effect;
        var p = PreparedAdjustments.From(settings);
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["exposureGain"] = p.ExposureGain,
            ["contrastGamma"] = p.ContrastGamma,
            ["highlightsAmount"] = p.HighlightsAmount,
            ["shadowsAmount"] = p.ShadowsAmount,
            ["whitesAmount"] = p.WhitesAmount,
            ["blacksAmount"] = p.BlacksAmount,
        };
        using var imageShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        var children = new SKRuntimeEffectChildren(effect)
        {
            ["image"] = imageShader,
        };
        return effect.ToShader(uniforms, children);
    }

    /// <summary>
    /// Runs the shader on Skia's CPU raster backend. Used by tests to compare the shader
    /// with <see cref="CpuAdjustmentRenderer"/> without a GPU.
    /// </summary>
    public static SKBitmap RenderRaster(SKBitmap source, AdjustmentSettings settings)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(source);
        using var surface = SKSurface.Create(info);
        using var shader = CreateShader(image, settings, new SKSamplingOptions(SKFilterMode.Nearest));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        var result = new SKBitmap(info);
        surface.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0);
        return result;
    }
}
