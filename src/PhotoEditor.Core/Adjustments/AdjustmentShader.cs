using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
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
        // Where to apply this pass (red channel, 0..1); the global pass uses solid white.
        uniform shader mask;
        uniform float exposureGain;
        uniform float contrastGamma;
        uniform float highlightsAmount;
        uniform float shadowsAmount;
        uniform float whitesAmount;
        uniform float blacksAmount;
        uniform float3 whiteBalance;
        uniform float saturationFactor;
        uniform float vibranceAmount;
        // Per HSL band: hue shift (degrees), saturation factor, luminance (stops).
        uniform float3 hsl[8];
        // Band centre hues in degrees; hslCenters[8] = 360 closes the circle.
        uniform float hslCenters[9];

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

        float3 rgbToHsv(float3 c) {
            float mx = max(c.r, max(c.g, c.b));
            float mn = min(c.r, min(c.g, c.b));
            float d = mx - mn;
            float s = mx > 0.0 ? d / mx : 0.0;
            if (d <= 0.0) return float3(0.0, s, mx);
            float h;
            if (mx == c.r) h = 60.0 * ((c.g - c.b) / d);
            else if (mx == c.g) h = 60.0 * ((c.b - c.r) / d + 2.0);
            else h = 60.0 * ((c.r - c.g) / d + 4.0);
            if (h < 0.0) h += 360.0;
            return float3(h, s, mx);
        }

        float hsvChannel(float n, float3 hsv) {
            float k = mod(n + hsv.x / 60.0, 6.0);
            return hsv.z - hsv.z * hsv.y * max(0.0, min(k, min(4.0 - k, 1.0)));
        }

        float3 hslAdjustment(float h) {
            float3 result = hsl[0];
            for (int i = 0; i < 8; i++) {
                if (h >= hslCenters[i] && h < hslCenters[i + 1]) {
                    float t = smoothstep(0.0, 1.0, (h - hslCenters[i]) / (hslCenters[i + 1] - hslCenters[i]));
                    float3 next = i == 7 ? hsl[0] : hsl[i + 1];
                    result = mix(hsl[i], next, t);
                }
            }
            return result;
        }

        float3 applyHsl(float3 c) {
            float3 hsv = rgbToHsv(pow(max(c, 0.0), float3(1.0 / perceptualGamma)));
            float3 adj = hslAdjustment(hsv.x);
            float s = hsv.y;
            float h2 = hsv.x + adj.x;
            if (h2 < 0.0) h2 += 360.0;
            if (h2 >= 360.0) h2 -= 360.0;
            float3 hsv2 = float3(h2, clamp(s * adj.y, 0.0, 1.0), hsv.z);
            float3 e = float3(hsvChannel(5.0, hsv2), hsvChannel(3.0, hsv2), hsvChannel(1.0, hsv2));
            return pow(e, float3(perceptualGamma)) * exp2(adj.z * s);
        }

        half4 main(float2 coord) {
            half4 src = image.eval(coord);
            float a = src.a;
            if (a <= 0.0) return half4(0);
            float3 s = float3(src.rgb) / a;

            // Linear light
            float3 c = float3(srgbToLinear(s.r), srgbToLinear(s.g), srgbToLinear(s.b));
            c *= whiteBalance * exposureGain;

            // Tone curve on perceptual luminance, applied as a ratio
            float y = dot(c, float3(0.2126, 0.7152, 0.0722));
            float y2 = pow(toneCurve(pow(max(y, 0.0), 1.0 / perceptualGamma)), perceptualGamma);
            c = y > 1e-6 ? c * (y2 / y) : float3(y2);

            // Vibrance then saturation around luminance
            float mx = max(c.r, max(c.g, c.b));
            float mn = min(c.r, min(c.g, c.b));
            float sat = mx > 1e-6 ? (mx - mn) / mx : 0.0;
            float factor = (1.0 + vibranceAmount * (1.0 - sat)) * saturationFactor;
            y = dot(c, float3(0.2126, 0.7152, 0.0722));
            c = max(y + (c - y) * factor, 0.0);

            c = applyHsl(c);

            c = clamp(c, 0.0, 1.0);
            c = float3(linearToSrgb(c.r), linearToSrgb(c.g), linearToSrgb(c.b));
            c = mix(s, c, float(mask.eval(coord).r));
            return half4(half3(c * a), half(a));
        }
        """;

    private static readonly float[] HslCentersUniform = [.. HslBands.Centers, 360f];

    private static readonly Lazy<SKRuntimeEffect> LazyEffect = new(() =>
        SKRuntimeEffect.CreateShader(Source, out var errors)
            ?? throw new InvalidOperationException("Adjustment shader failed to compile: " + errors));

    public static SKRuntimeEffect Effect => LazyEffect.Value;

    /// <summary>Creates the adjustment shader for <paramref name="image"/> in the image's pixel coordinates.</summary>
    public static SKShader CreateShader(SKImage image, AdjustmentSettings settings, SKSamplingOptions sampling) =>
        CreateShader(image, new EditState { Adjustments = settings }, sampling, _ => null);

    /// <summary>
    /// Creates the full pipeline: a global pass, then one pass per active mask, each blending its
    /// adjusted result with the previous one by the mask. <paramref name="maskImage"/> supplies the
    /// rasterised mask (any resolution; it is stretched over the image); null skips that mask.
    /// </summary>
    public static SKShader CreateShader(SKImage image, EditState state, SKSamplingOptions sampling, Func<Mask, SKImage?> maskImage)
    {
        using var imageShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var white = SKShader.CreateColor(SKColors.White);
        var current = CreatePass(imageShader, white, state.Adjustments);

        foreach (var mask in state.Masks)
        {
            if (!mask.IsActive || maskImage(mask) is not { } maskImg)
                continue;
            var toImage = SKMatrix.CreateScale((float)image.Width / maskImg.Width, (float)image.Height / maskImg.Height);
            using var maskShader = maskImg.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                MaskSampling(maskImg, image), toImage);
            var next = CreatePass(current, maskShader, mask.Adjustments);
            current.Dispose();
            current = next;
        }
        return current;
    }

    // Exact pixels when the mask matches the image (tests, export); smooth when it is stretched.
    private static SKSamplingOptions MaskSampling(SKImage mask, SKImage image) =>
        mask.Width == image.Width && mask.Height == image.Height
            ? new SKSamplingOptions(SKFilterMode.Nearest)
            : new SKSamplingOptions(SKFilterMode.Linear);

    private static SKShader CreatePass(SKShader input, SKShader mask, AdjustmentSettings settings)
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
            ["whiteBalance"] = new[] { p.WhiteBalanceR, p.WhiteBalanceG, p.WhiteBalanceB },
            ["saturationFactor"] = p.SaturationFactor,
            ["vibranceAmount"] = p.VibranceAmount,
            ["hsl"] = p.Hsl,
            ["hslCenters"] = HslCentersUniform,
        };
        var children = new SKRuntimeEffectChildren(effect)
        {
            ["image"] = input,
            ["mask"] = mask,
        };
        return effect.ToShader(uniforms, children);
    }

    /// <summary>
    /// Runs the shader on Skia's CPU raster backend. Used by tests to compare the shader
    /// with <see cref="CpuAdjustmentRenderer"/> without a GPU.
    /// </summary>
    public static SKBitmap RenderRaster(SKBitmap source, AdjustmentSettings settings) =>
        RenderRaster(source, new EditState { Adjustments = settings });

    /// <summary>As above, with masks rasterised at the image size.</summary>
    public static SKBitmap RenderRaster(SKBitmap source, EditState state)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(source);
        using var surface = SKSurface.Create(info);
        var masks = state.Masks.Where(m => m.IsActive).ToDictionary(
            m => m.Id, m => SKImage.FromBitmap(MaskRasterizer.RasterizeToBitmap(m, source.Width, source.Height)));
        using var shader = CreateShader(image, state, new SKSamplingOptions(SKFilterMode.Nearest), m => masks[m.Id]);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        var result = new SKBitmap(info);
        surface.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0);
        foreach (var m in masks.Values)
            m.Dispose();
        return result;
    }
}
