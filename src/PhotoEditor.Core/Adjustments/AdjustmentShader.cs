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

        float srgbToLinear(float c) {
            return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
        }
        float linearToSrgb(float c) {
            return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
        }

        half4 main(float2 coord) {
            half4 src = image.eval(coord);
            float a = src.a;
            if (a <= 0.0) return half4(0);
            float3 c = float3(src.rgb) / a;

            // Linear light
            c = float3(srgbToLinear(c.r), srgbToLinear(c.g), srgbToLinear(c.b));
            c *= exposureGain;

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
