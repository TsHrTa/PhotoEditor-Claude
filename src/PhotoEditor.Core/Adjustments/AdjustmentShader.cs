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
        // The original photo (all passes): Soften removes its fine detail.
        uniform shader source;
        // Where to apply this pass (red channel, 0..1); the global pass uses solid white.
        uniform shader mask;
        uniform float exposureGain;
        uniform float contrastGamma;
        uniform float highlightsAmount;
        uniform float shadowsAmount;
        uniform float whitesAmount;
        uniform float blacksAmount;
        // Second passes: the part of each slider beyond ±100 (0 otherwise).
        uniform float highlightsAmount2;
        uniform float shadowsAmount2;
        uniform float whitesAmount2;
        uniform float blacksAmount2;
        uniform float3 whiteBalance;
        uniform float saturationFactor;
        uniform float vibranceAmount;
        // Per HSL band: hue shift (degrees), saturation factor, luminance (stops).
        uniform float3 hsl[8];
        // Band centre hues in degrees; hslCenters[8] = 360 closes the circle.
        uniform float hslCenters[9];
        // Vignette: exposure change at full weight, transition band, roundness; the frame it follows
        // (crop centre, half size and (cos, sin) of its angle, in image pixels).
        uniform float vignetteStops;
        uniform float vignetteLow;
        uniform float vignetteHigh;
        uniform float vignetteRoundness;
        uniform float2 vignetteCenter;
        uniform float2 vignetteHalf;
        uniform float2 vignetteRotation;
        // Unsharp mask (global pass only): strength, blur tap spacing in image pixels, edge masking 0..1.
        uniform float sharpenAmount;
        uniform float sharpenStep;
        uniform float sharpenMasking;
        // Soften: amount 0..1 and blur tap spacing in image pixels.
        uniform float softenAmount;
        uniform float softenStep;
        const float softenRangeSigma = 0.1;

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

        float highlightsStep(float x, float a) { return x + a * smoothstep(0.35, 1.0, x); }
        float shadowsStep(float x, float a) {
            float xs = clamp(x, 0.0, 1.0);
            return x + a * 6.75 * xs * (1.0 - xs) * (1.0 - xs);
        }
        float whitesStep(float x, float a) {
            float xs = clamp(x, 0.0, 1.0);
            return x + a * xs * xs * xs * xs;
        }
        float blacksStep(float x, float a) {
            float inv = 1.0 - clamp(x, 0.0, 1.0);
            return x + a * inv * inv * inv * inv;
        }

        float toneCurve(float x) {
            x = contrastCurve(x, contrastGamma);
            x = highlightsStep(highlightsStep(x, highlightsAmount), highlightsAmount2);
            x = shadowsStep(shadowsStep(x, shadowsAmount), shadowsAmount2);
            x = whitesStep(whitesStep(x, whitesAmount), whitesAmount2);
            x = blacksStep(blacksStep(x, blacksAmount), blacksAmount2);
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

        float vignetteDistance(float2 uv) {
            if (vignetteRoundness >= 0.0) {
                float ellipse = sqrt(uv.x * uv.x + uv.y * uv.y) / sqrt(2.0);
                float2 size = vignetteHalf * 2.0;
                float2 px = uv * size;
                float circle = sqrt(px.x * px.x + px.y * px.y) / sqrt(size.x * size.x + size.y * size.y);
                return ellipse + (circle - ellipse) * vignetteRoundness;
            }
            float p = 2.0 - 6.0 * vignetteRoundness;
            return pow(pow(abs(uv.x), p) + pow(abs(uv.y), p), 1.0 / p) / pow(2.0, 1.0 / p);
        }

        // 5 × 5 Gaussian (sigma = one tap step) around coord; returns unpremultiplied colour.
        float3 blurred(float2 coord, float3 fallback) {
            float4 sum = float4(0);
            float wsum = 0.0;
            for (int j = -2; j <= 2; j++) {
                for (int i = -2; i <= 2; i++) {
                    float w = exp(-0.5 * float(i * i + j * j));
                    sum += w * float4(image.eval(coord + float2(float(i), float(j)) * sharpenStep));
                    wsum += w;
                }
            }
            sum /= wsum;
            return sum.a > 0.0 ? sum.rgb / sum.a : fallback;
        }

        float3 sharpen(float3 s, float2 coord) {
            float detail = dot(s - blurred(coord, s), float3(0.2126, 0.7152, 0.0722));
            float weight = sharpenMasking > 0.0 ? smoothstep(sharpenMasking * 0.02, sharpenMasking * 0.08, abs(detail)) : 1.0;
            return clamp(s + sharpenAmount * detail * weight, 0.0, 1.0);
        }

        // Fine detail of the original: the pixel minus an edge-preserving (bilateral) 7 × 7 blur around it
        // (spatial sigma 1.5 taps, range sigma on luminance), so strong edges keep their contrast.
        float3 softDetail(float2 coord) {
            half4 c0 = source.eval(coord);
            if (c0.a <= 0.0) return float3(0);
            float3 center = float3(c0.rgb) / c0.a;
            float yc = dot(center, float3(0.2126, 0.7152, 0.0722));
            float3 sum = float3(0);
            float wsum = 0.0;
            for (int j = -3; j <= 3; j++) {
                for (int i = -3; i <= 3; i++) {
                    half4 t = source.eval(coord + float2(float(i), float(j)) * softenStep);
                    float3 c = t.a > 0.0 ? float3(t.rgb) / t.a : center;
                    float d = dot(c, float3(0.2126, 0.7152, 0.0722)) - yc;
                    float w = t.a > 0.0
                        ? exp(-float(i * i + j * j) / 4.5) * exp(-d * d / (2.0 * softenRangeSigma * softenRangeSigma))
                        : 0.0;
                    sum += w * c;
                    wsum += w;
                }
            }
            return center - sum / wsum;
        }

        half4 main(float2 coord) {
            half4 src = image.eval(coord);
            float a = src.a;
            if (a <= 0.0) return half4(0);
            float3 s = float3(src.rgb) / a;
            // What this pass leaves outside its mask.
            float3 s0 = s;
            if (sharpenAmount > 0.0) s = sharpen(s, coord);
            if (softenAmount > 0.0) s = clamp(s - softenAmount * softDetail(coord), 0.0, 1.0);

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

            if (vignetteStops != 0.0) {
                float2 d = coord - vignetteCenter;
                float2 uv = float2(d.x * vignetteRotation.x + d.y * vignetteRotation.y,
                                   -d.x * vignetteRotation.y + d.y * vignetteRotation.x) / vignetteHalf;
                c *= exp2(vignetteStops * smoothstep(vignetteLow, vignetteHigh, vignetteDistance(uv)));
            }

            c = clamp(c, 0.0, 1.0);
            c = float3(linearToSrgb(c.r), linearToSrgb(c.g), linearToSrgb(c.b));
            c = mix(s0, c, float(mask.eval(coord).r));
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
    /// <remarks>
    /// <paramref name="pixelScale"/> = pixels of <paramref name="image"/> per full-resolution pixel (the preview
    /// is smaller), so the sharpening radius stays the same relative to the photo.
    /// </remarks>
    public static SKShader CreateShader(SKImage image, EditState state, SKSamplingOptions sampling, Func<Mask, SKImage?> maskImage,
        double pixelScale = 1)
    {
        using var imageShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var white = SKShader.CreateColor(SKColors.White);
        var frame = VignetteMath.Frame.From(state.Crop.Frame(image.Width, image.Height));
        // Soften taps are a whole number of full-resolution pixels apart (as on the CPU at export).
        int fullLongSide = (int)Math.Round(Math.Max(image.Width, image.Height) / pixelScale);
        float softenStep = (float)(PreparedAdjustments.SoftenStepFor(fullLongSide) * pixelScale);
        var current = CreatePass(imageShader, imageShader, white, state.Adjustments, frame, (float)pixelScale, softenStep, sharpen: true);

        foreach (var mask in state.Masks)
        {
            if (!mask.IsActive || maskImage(mask) is not { } maskImg)
                continue;
            var toImage = SKMatrix.CreateScale((float)image.Width / maskImg.Width, (float)image.Height / maskImg.Height);
            using var maskShader = maskImg.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                MaskSampling(maskImg, image), toImage);
            var next = CreatePass(current, imageShader, maskShader, mask.Adjustments, frame, (float)pixelScale, softenStep, sharpen: false);
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

    private static SKShader CreatePass(SKShader input, SKShader source, SKShader mask, AdjustmentSettings settings, VignetteMath.Frame frame,
        float pixelScale, float softenStep, bool sharpen)
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
            ["highlightsAmount2"] = p.HighlightsAmount2,
            ["shadowsAmount2"] = p.ShadowsAmount2,
            ["whitesAmount2"] = p.WhitesAmount2,
            ["blacksAmount2"] = p.BlacksAmount2,
            ["whiteBalance"] = new[] { p.WhiteBalanceR, p.WhiteBalanceG, p.WhiteBalanceB },
            ["saturationFactor"] = p.SaturationFactor,
            ["vibranceAmount"] = p.VibranceAmount,
            ["hsl"] = p.Hsl,
            ["hslCenters"] = HslCentersUniform,
            ["vignetteStops"] = p.VignetteStops,
            ["vignetteLow"] = p.VignetteLow,
            ["vignetteHigh"] = p.VignetteHigh,
            ["vignetteRoundness"] = p.VignetteRoundness,
            ["vignetteCenter"] = new[] { frame.CenterX, frame.CenterY },
            ["vignetteHalf"] = new[] { frame.HalfWidth, frame.HalfHeight },
            ["vignetteRotation"] = new[] { frame.Cos, frame.Sin },
            ["sharpenAmount"] = sharpen ? p.SharpenAmount : 0f,
            ["sharpenStep"] = p.SharpenRadius * pixelScale,
            ["sharpenMasking"] = p.SharpenMasking,
            ["softenAmount"] = p.SoftenAmount,
            ["softenStep"] = softenStep,
        };
        var children = new SKRuntimeEffectChildren(effect)
        {
            ["image"] = input,
            ["source"] = source,
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
        // Linear sampling: identical to nearest at pixel centres, and matches the CPU's bilinear sharpening taps.
        using var shader = CreateShader(image, state, new SKSamplingOptions(SKFilterMode.Linear), m => masks[m.Id]);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        surface.Canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        var result = new SKBitmap(info);
        surface.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0);
        foreach (var m in masks.Values)
            m.Dispose();
        return result;
    }
}
