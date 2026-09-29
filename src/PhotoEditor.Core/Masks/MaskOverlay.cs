using SkiaSharp;

namespace PhotoEditor.Core.Masks;

/// <summary>Red semi-transparent tint showing where a mask applies (drawn over the image).</summary>
public static class MaskOverlay
{
    public const float Opacity = 0.5f;

    public const string Source = """
        uniform shader mask;
        uniform float opacity;

        half4 main(float2 coord) {
            float m = mask.eval(coord).r * opacity;
            return half4(half(m), 0, 0, half(m)); // premultiplied red
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> LazyEffect = new(() =>
        SKRuntimeEffect.CreateShader(Source, out var errors)
            ?? throw new InvalidOperationException("Mask overlay shader failed to compile: " + errors));

    /// <summary>Overlay shader in the coordinate space of an image of <paramref name="imageWidth"/> × <paramref name="imageHeight"/>.</summary>
    public static SKShader CreateShader(SKImage maskImage, int imageWidth, int imageHeight)
    {
        var effect = LazyEffect.Value;
        var toImage = SKMatrix.CreateScale((float)imageWidth / maskImage.Width, (float)imageHeight / maskImage.Height);
        using var maskShader = maskImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear), toImage);
        var uniforms = new SKRuntimeEffectUniforms(effect) { ["opacity"] = Opacity };
        var children = new SKRuntimeEffectChildren(effect) { ["mask"] = maskShader };
        return effect.ToShader(uniforms, children);
    }
}
