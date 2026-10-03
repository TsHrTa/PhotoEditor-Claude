using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Imaging;
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
        uniform float sceneExposure;
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
        // 1 when any band has an adjustment (otherwise the pass skips the HSL panel).
        uniform float hslOn;
        // Band centre hues in degrees relative to the first band (red, whose OKLCh hue is hslOrigin); hslCenters[8] = 360 closes the circle.
        uniform float hslCenters[9];
        uniform float hslOrigin;
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
        // Dehaze: transmission map (red, stretched over the image), haze colour (linear), amount −1..1.
        uniform shader haze;
        uniform float3 hazeLight;
        uniform float dehazeAmount;
        // Noise reduction and defringe (global pass only): amounts 0..1 and tap spacings in image pixels.
        uniform float noiseLumaAmount;
        uniform float noiseColorAmount;
        uniform float defringePurple;
        uniform float defringeGreen;
        // Hue ranges (degrees) of the purple / green defringe.
        uniform float2 purpleHues;
        uniform float2 greenHues;
        uniform float lumaNoiseStep;
        uniform float colorNoiseStep;
        uniform float fringeStep;
        // Local highlights / shadows: the photo's base-brightness coefficients (a in red, (b + 16) / 16 in green),
        // stretched over the image; useToneBase = 1 when bound.
        uniform shader toneBase;
        uniform float useToneBase;
        // RAW highlights above white (see Headroom): value = 1 + headroomScale × stored, 0 = none. The global pass
        // adds it to the photo (addHeadroom = 1); every pass uses it for the base brightness.
        uniform shader headroom;
        uniform float headroomScale;
        uniform float addHeadroom;
        // The fraction of an 8-bit step the photo rounded away (see Headroom.Fine): value = (stored − 128/255) × 255
        // × fineStep; added in the global pass when addFine = 1.
        uniform shader fine;
        uniform float addFine;
        // Lens vignetting (global pass): the profile's vignetting removed (lensTable: gain by r², 256 × 1 texels,
        // used when lensTableOn = 1), the manual amount (±1 stop at the corner) applied; r = 1 at the corner of the
        // image (centre and 1 / half-diagonal² in image pixels).
        uniform shader lensTable;
        uniform float lensTableOn;
        uniform float lensVignetteManual;
        uniform float lensVignetteOn;
        uniform float2 lensCenter;
        uniform float lensInvHalfDiagonal2;
        // The Transform's map from an output position (from lensCenter, in half-diagonals) to the frame the table is
        // made for (rows of a 3 × 3 projective matrix), used when lensMapOn = 1.
        uniform float3 lensRow0;
        uniform float3 lensRow1;
        uniform float3 lensRow2;
        uniform float lensMapOn;
        // Brightest linear value this pass hands on: 1 for the last pass, more between passes so a mask can
        // still bring back what an earlier pass pushed above white.
        uniform float outputLimit;
        uniform float softenAmount;
        uniform float softenStep;
        // Texture: amount (detail added, negative smooths) and the small blur's tap spacing (1 full-resolution pixel).
        uniform float textureAmount;
        uniform float textureStep;
        // Clarity: extra local contrast in stops per stop (see clarityGain).
        uniform float clarityAmount;
        // Tone curve panel (global pass): per channel lookup on perceptual values, 1024 × 1 texels (red, green,
        // blue), used when curveOn = 1.
        uniform shader curveTable;
        uniform float curveOn;
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

        // RawBaseCurve (constants: RawBaseCurve.Exposure / Toe) and Exposure applied before it.
        float rawCurve(float x) {
            float z = max(x, 0.0) * 4.4;
            float y = z * (1.0 + z / (7.04 * 7.04)) / (1.0 + z);
            return y * y * (1.0 + 0.012) / (y + 0.012);
        }
        float rawCurveInverse(float y) {
            y = max(y, 0.0);
            float u = (y + sqrt(y * y + 4.0 * (1.0 + 0.012) * 0.012 * y)) / (2.0 * (1.0 + 0.012));
            float z = 2.0 * u / (1.0 - u + sqrt((1.0 - u) * (1.0 - u) + 4.0 * u / (7.04 * 7.04)));
            return z / 4.4;
        }
        // Per-channel gains (white balance x exposure) on the scene values before the base curve (RawBaseCurve.ApplyScene).
        float3 rawScene(float3 c, float3 gains) {
            float hi = max(c.r, max(c.g, c.b));
            float lo = max(min(c.r, min(c.g, c.b)), 0.0);
            if (hi <= 0.0) return c;
            float hs = rawCurveInverse(hi);
            float ls = rawCurveInverse(lo);
            float3 s = float3(hs);
            if (hi - lo >= 1e-7) s = ls + (max(c, 0.0) - lo) * ((hs - ls) / (hi - lo));
            s *= gains;
            float hi2 = max(s.r, max(s.g, s.b));
            float lo2 = min(s.r, min(s.g, s.b));
            float h = rawCurve(hi2);
            float l = rawCurve(lo2);
            if (hi2 - lo2 < 1e-9) return float3(h);
            return l + (s - lo2) * ((h - l) / (hi2 - lo2));
        }        float highlightsStep(float x, float a) {
            if (x <= 0.4 || (a > 0.0 && x >= 1.0)) return x;
            if (x < 1.0) {
                float t = (x - 0.4) / 0.6;
                if (a < 0.0) return x + a * t * t * (2.0 - t);
                float h = 1.0 - pow(1.0 - t, 1.0 + 12.0 * a);
                return 0.4 + (t + t * (h - t)) * 0.6;
            }
            float d = x - 1.0;
            float slope = 1.0 + a / 0.6;
            return 1.0 + a + slope * d / (1.0 - 10.0 * a * d);
        }
        // A brightening gain limited so the brightest channel approaches white softly (see ToneCurve.LimitBrightening).
        float limitBrightening(float hi, float gain) {
            float v = hi * gain;
            if (gain <= 1.0 || hi <= 0.0 || v <= 0.6) return gain;
            float s = 0.6 + 0.4 * (1.0 - exp(-(v - 0.6) / 0.4));
            return max(s / hi, 1.0);
        }
        // Colours above white fade to white before the final clip (see ToneCurve.RollToWhite).
        float3 rollToWhite(float3 c) {
            float hi = max(c.r, max(c.g, c.b));
            if (hi <= 1.0) return c;
            float e = hi - 1.0;
            return c + (hi - c) * (e * e / (e * e + 1.0));
        }
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

        // The global curve: contrast, whites, blacks (highlights / shadows are local, see localGain).
        float toneCurve(float x) {
            x = contrastCurve(x, contrastGamma);
            x = whitesStep(whitesStep(x, whitesAmount), whitesAmount2);
            x = blacksStep(blacksStep(x, blacksAmount), blacksAmount2);
            return max(x, 0.0);
        }

        // Saturation boost applied in full up to the sRGB edge, 70 % of the rest beyond it (see SaturationMath.LimitFactor).
        float limitSaturation(float factor, float luminance, float minChannel) {
            if (factor <= 1.0 || minChannel >= luminance || luminance <= 0.0) return factor;
            float edge = max(luminance / (luminance - minChannel), 1.0);
            return factor <= edge ? factor : edge + 0.7 * (factor - edge);
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

        // Cube root: pow, then one Newton step (the GPU pow is only accurate to about 1e-3).
        float cbrt(float x) {
            if (x <= 0.0) return 0.0;
            float y = pow(x, 1.0 / 3.0);
            return y - (y * y * y - x) / (3.0 * y * y);
        }
        // Linear sRGB to OKLab (Bjorn Ottosson) and back; the HSL panel works in OKLCh (hue moves keep brightness).
        float3 toOklab(float3 c) {
            float l = cbrt(max(0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b, 0.0));
            float m = cbrt(max(0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b, 0.0));
            float s = cbrt(max(0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b, 0.0));
            return float3(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                          1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                          0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }
        float3 fromOklab(float3 lab) {
            float l = lab.x + 0.3963377774 * lab.y + 0.2158037573 * lab.z;
            float m = lab.x - 0.1055613458 * lab.y - 0.0638541728 * lab.z;
            float s = lab.x - 0.0894841775 * lab.y - 1.2914855480 * lab.z;
            l = l * l * l;
            m = m * m * m;
            s = s * s * s;
            return float3(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                          -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                          -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        }

        float3 fitGamut(float3 lab) {
            float3 rgb = fromOklab(lab);
            if (min(rgb.r, min(rgb.g, rgb.b)) >= -0.0005) return max(rgb, 0.0);
            float lo = 0.0;
            float hi = 1.0;
            for (int i = 0; i < 10; i++) {
                float mid = (lo + hi) * 0.5;
                float3 t = fromOklab(float3(lab.x, lab.y * mid, lab.z * mid));
                if (min(t.r, min(t.g, t.b)) >= -0.0005) lo = mid; else hi = mid;
            }
            return max(fromOklab(float3(lab.x, lab.y * lo, lab.z * lo)), 0.0);
        }

        float3 applyHsl(float3 c) {
            float3 lab = toOklab(c);
            float chroma = length(lab.yz);
            // Greys have no hue (and atan(0, 0) is undefined): nothing to adjust.
            if (chroma < 1e-5) return c;
            float hue = degrees(atan(lab.z, lab.y));
            if (hue < 0.0) hue += 360.0;
            float3 adj = hslAdjustment(mod(hue - hslOrigin + 360.0, 360.0));
            // Hue shift = a rotation of (a, b) and saturation = a scale of it: the original hue never goes through
            // sin / cos (approximate on some backends, and a saturated blue is very sensitive to it).
            float th = radians(adj.x);
            float ct = cos(th);
            float st = sin(th);
            float a2 = adj.y * (lab.y * ct - lab.z * st);
            float b2 = adj.y * (lab.y * st + lab.z * ct);
            // A colour outside sRGB loses chroma at the same lightness and hue (see GamutMapping).
            float3 rgb = fitGamut(float3(lab.x, a2, b2));
            // The luminance change is weighted by chroma so greys stay untouched.
            return rgb * exp2(adj.z * clamp(chroma / 0.15, 0.0, 1.0));
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

        // 5 × 5 Gaussian (sigma = one tap step) of the original photo around coord; returns unpremultiplied colour.
        // It samples `source`, never `image`: in mask passes `image` is the whole chain of previous passes, and
        // GPU compilers (e.g. Direct3D's) inline every child call, so 25 taps per pass would multiply the shader's
        // size with each mask (a freeze while compiling with two brush masks). Only `main` evaluates `image`, once.
        float3 blurred(float2 coord, float3 fallback) {
            float4 sum = float4(0);
            float wsum = 0.0;
            for (int j = -2; j <= 2; j++) {
                for (int i = -2; i <= 2; i++) {
                    float w = exp(-0.5 * float(i * i + j * j));
                    sum += w * float4(source.eval(coord + float2(float(i), float(j)) * sharpenStep));
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

        float3 dehaze(float3 c, float2 coord) {
            if (dehazeAmount > 0.0) {
                float t = float(haze.eval(coord).r);
                float tt = max(1.0 - dehazeAmount * (1.0 - t), 0.1);
                return max((c - hazeLight) / tt + hazeLight, 0.0);
            }
            return c + (hazeLight - c) * (-dehazeAmount * 0.6);
        }

        float lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

        // The original photo at p, unpremultiplied; transparent pixels count as the centre colour.
        float3 sourceAt(float2 p, float3 fallback) {
            half4 t = source.eval(p);
            return t.a > 0.0 ? float3(t.rgb) / t.a : fallback;
        }

        // Luminance noise at coord: brightness minus its non-local-means estimate: the weighted mean of the 5 x 5
        // neighbours, each weighted by how well its 3 x 3 brightness patch matches the centre's patch (the mean
        // squared difference minus the part noise alone explains, range h^2 = 0.004), with a mild spatial falloff.
        // The same change is subtracted from all channels so colours are kept.
        float lumaNoise(float2 coord, float3 c0) {
            float cp[9];
            for (int pj = 0; pj < 3; pj++) {
                for (int pi = 0; pi < 3; pi++) {
                    cp[pj * 3 + pi] = lum(sourceAt(coord + float2(float(pi - 1), float(pj - 1)) * lumaNoiseStep, c0));
                }
            }
            float y0 = cp[4];
            float sum = y0;
            float wsum = 1.0;
            for (int j = -2; j <= 2; j++) {
                for (int i = -2; i <= 2; i++) {
                    if (i == 0 && j == 0) continue;
                    float dist = 0.0;
                    float yc = 0.0;
                    for (int pj = 0; pj < 3; pj++) {
                        for (int pi = 0; pi < 3; pi++) {
                            float yy = lum(sourceAt(coord + float2(float(i + pi - 1), float(j + pj - 1)) * lumaNoiseStep, c0));
                            float d = yy - cp[pj * 3 + pi];
                            dist += d * d;
                            if (pi == 1 && pj == 1) yc = yy;
                        }
                    }
                    float w = exp(-max(dist / 9.0 - 0.0006, 0.0) / 0.004) * exp(-float(i * i + j * j) / 8.0);
                    sum += w * yc;
                    wsum += w;
                }
            }
            return y0 - sum / wsum;
        }

        // Colour noise at coord: the chroma (colour minus brightness) minus its 5 × 5 average (spatial sigma 1.5 taps),
        // guarded by brightness edges (range sigma 0.1) so colours don't bleed across edges.
        float3 colorNoise(float2 coord, float3 c0) {
            float y0 = lum(c0);
            float3 sum = float3(0);
            float wsum = 0.0;
            for (int j = -2; j <= 2; j++) {
                for (int i = -2; i <= 2; i++) {
                    float3 c = sourceAt(coord + float2(float(i), float(j)) * colorNoiseStep, c0);
                    float y = lum(c);
                    float d = y - y0;
                    float w = exp(-float(i * i + j * j) / 4.5) * exp(-d * d / 0.02);
                    sum += w * (c - y);
                    wsum += w;
                }
            }
            return (c0 - y0) - sum / wsum;
        }

        // Weight of hue h in [r.x, r.y] with 25° soft edges, across the 360° wrap.
        float hueBand(float h, float2 r) {
            return smoothstep(r.x - 25.0, r.x, h) * (1.0 - smoothstep(r.y, r.y + 25.0, h));
        }
        float hueRange(float h, float2 r) {
            return max(hueBand(h, r), max(hueBand(h + 360.0, r), hueBand(h - 360.0, r)));
        }

        // Defringe: desaturates purple / green pixels near a brightness edge (largest difference to the 8 neighbours
        // at fringeStep and at 3 × fringeStep). Returns the change to add.
        float3 defringe(float2 coord, float3 c0) {
            float y0 = lum(c0);
            float edgeDiff = 0.0;
            for (int ring = 1; ring <= 3; ring += 2) {
                for (int j = -1; j <= 1; j++) {
                    for (int i = -1; i <= 1; i++) {
                        float y = lum(sourceAt(coord + float2(float(i), float(j)) * (fringeStep * float(ring)), c0));
                        edgeDiff = max(edgeDiff, abs(y - y0));
                    }
                }
            }
            float edge = smoothstep(0.04, 0.15, edgeDiff);
            float h = rgbToHsv(c0).x;
            float purple = hueRange(h, purpleHues);
            float green = hueRange(h, greenHues);
            float k = clamp((defringePurple * purple + defringeGreen * green) * edge * 3.0, 0.0, 1.0);
            return -k * (c0 - y0);
        }

        // Base (area) brightness over the pixel's own brightness, both from the original photo.
        float baseRatioAt(float2 coord) {
            if (useToneBase < 0.5) return 1.0;
            half4 o = source.eval(coord);
            float l = log2(1e-4);
            if (o.a > 0.0) {
                float3 c = min(float3(o.rgb) / o.a, 1.0) + headroomScale * float3(headroom.eval(coord).rgb);
                l = log2(max(dot(float3(srgbToLinear(c.r), srgbToLinear(c.g), srgbToLinear(c.b)),
                    float3(0.2126, 0.7152, 0.0722)), 1e-4));
            }
            float4 ab = float4(toneBase.eval(coord));
            return exp2(ab.r * l + (ab.g * 16.0 - 16.0) - l);
        }

        // Medium-size detail for Texture, part 1: luminance of a 3 × 3 (1-2-1) blur of the original minus the pixel's.
        float smallBlurMinusCenter(float2 coord) {
            half4 c0 = source.eval(coord);
            if (c0.a <= 0.0) return 0.0;
            float3 center = float3(c0.rgb) / c0.a;
            float sum = 0.0;
            for (int j = -1; j <= 1; j++) {
                for (int i = -1; i <= 1; i++) {
                    float w = (2.0 - abs(float(i))) * (2.0 - abs(float(j))) / 16.0;
                    sum += w * lum(sourceAt(coord + float2(float(i), float(j)) * textureStep, center));
                }
            }
            return sum - lum(center);
        }

        // Clarity as a factor: the pixel's contrast against its area (pixel / base) scaled, mostly in the midtones.
        float clarityGain(float baseLuminance, float ratio) {
            float x = pow(max(baseLuminance, 0.0), 1.0 / perceptualGamma);
            float weight = clamp(4.0 * x * (1.0 - x), 0.0, 1.0);
            return clamp(pow(max(ratio, 1e-6), -clarityAmount * weight), 0.25, 4.0);
        }

        // Highlights / shadows as a factor: the curve moves the base brightness, the pixel follows (detail kept).
        float localGain(float baseLuminance) {
            float yb = max(baseLuminance, 1e-6);
            float x = pow(yb, 1.0 / perceptualGamma);
            x = highlightsStep(highlightsStep(x, highlightsAmount), highlightsAmount2);
            x = shadowsStep(shadowsStep(x, shadowsAmount), shadowsAmount2);
            float y2 = pow(max(x, 0.0), perceptualGamma);
            return clamp(y2 / yb, 0.125, 8.0);
        }

        // The tone curve table on each channel; values above 1 keep their distance above the curve's end.
        float3 applyCurve(float3 c) {
            float3 x = pow(max(c, 0.0), float3(1.0 / perceptualGamma));
            float3 t = clamp(x, 0.0, 1.0) * 1023.0 + 0.5;
            float3 v = float3(float(curveTable.eval(float2(t.r, 0.5)).r), float(curveTable.eval(float2(t.g, 0.5)).g),
                float(curveTable.eval(float2(t.b, 0.5)).b));
            return pow(v + max(x - 1.0, 0.0), float3(perceptualGamma));
        }

        half4 main(float2 coord) {
            half4 src = image.eval(coord);
            float a = src.a;
            if (a <= 0.0) return half4(0);
            float3 s = float3(src.rgb) / a;
            // What this pass leaves outside its mask.
            float3 s0 = s;
            if (sharpenAmount > 0.0) s = sharpen(s, coord);
            if (noiseLumaAmount > 0.0 || noiseColorAmount > 0.0 || defringePurple > 0.0 || defringeGreen > 0.0) {
                float3 c0 = sourceAt(coord, s);
                if (noiseLumaAmount > 0.0) s -= noiseLumaAmount * lumaNoise(coord, c0);
                if (noiseColorAmount > 0.0) s -= noiseColorAmount * colorNoise(coord, c0);
                if (defringePurple > 0.0 || defringeGreen > 0.0) s += defringe(coord, c0);
                s = clamp(s, 0.0, 1.0);
            }
            if (softenAmount > 0.0 || textureAmount != 0.0) {
                float3 sd = softDetail(coord);
                if (softenAmount > 0.0) s = clamp(s - softenAmount * sd, float3(0.0), max(s, float3(1.0)));
                if (textureAmount != 0.0) {
                    float t = textureAmount * (smallBlurMinusCenter(coord) + lum(sd));
                    s = clamp(s + t, float3(0.0), max(s, float3(1.0)));
                }
            }
            // Highlights above white (after the detail filters, which work on the 8-bit photo)
            if (addHeadroom > 0.0) s += headroomScale * float3(headroom.eval(coord).rgb);
            if (addFine > 0.0) s += (float3(fine.eval(coord).rgb) - 128.0 / 255.0) * (255.0 / 65025.0);

            // Linear light
            float3 c = float3(srgbToLinear(s.r), srgbToLinear(s.g), srgbToLinear(s.b));
            if (lensVignetteOn > 0.0) {
                float2 n = (coord - lensCenter) * sqrt(lensInvHalfDiagonal2);
                float r2 = dot(n, n);
                float2 m = n;
                if (lensMapOn > 0.0) {
                    float3 h = float3(n, 1.0);
                    m = float2(dot(lensRow0, h), dot(lensRow1, h)) / dot(lensRow2, h);
                }
                float gain = lensTableOn > 0.0 ? float(lensTable.eval(float2(clamp(dot(m, m), 0.0, 1.0) * 255.0 + 0.5, 0.5)).r) : 1.0;
                if (lensVignetteManual != 0.0) gain *= exp2(lensVignetteManual * r2);
                c *= gain;
            }
            if (dehazeAmount != 0.0) c = dehaze(c, coord);
            if (sceneExposure > 0.0) {
                float3 gains = whiteBalance * exposureGain;
                if (gains.r != 1.0 || gains.g != 1.0 || gains.b != 1.0) c = rawScene(c, gains);
            } else {
                c *= whiteBalance * exposureGain;
            }

            // Local highlights / shadows, then the tone curve on perceptual luminance, applied as a ratio
            float y = dot(c, float3(0.2126, 0.7152, 0.0722));
            float ratio = highlightsAmount != 0.0 || shadowsAmount != 0.0 || clarityAmount != 0.0 ? baseRatioAt(coord) : 1.0;
            if (highlightsAmount != 0.0 || shadowsAmount != 0.0) {
                float gain = localGain(y * ratio);
                if (highlightsAmount > 0.0) gain = limitBrightening(max(c.r, max(c.g, c.b)), gain);
                c *= gain;
                y *= gain;
                if (highlightsAmount < 0.0 && gain < 1.0) {
                    float k = 1.0 + 0.6 * (1.0 - clamp(gain, 0.0, 1.0));
                    c = max(y + (c - y) * k, 0.0);
                }
            }
            if (clarityAmount != 0.0) {
                float gain = clarityGain(y * ratio, ratio);
                c *= gain;
                y *= gain;
            }
            float y2 = pow(toneCurve(pow(max(y, 0.0), 1.0 / perceptualGamma)), perceptualGamma);
            c = y > 1e-6 ? c * (y2 / y) : float3(y2);
            if (curveOn > 0.0) c = applyCurve(c);

            // Vibrance then saturation around luminance
            float mx = max(c.r, max(c.g, c.b));
            float mn = min(c.r, min(c.g, c.b));
            float sat = mx > 1e-6 ? (mx - mn) / mx : 0.0;
            float factor = (1.0 + vibranceAmount * (1.0 - sat)) * saturationFactor;
            y = dot(c, float3(0.2126, 0.7152, 0.0722));
            factor = limitSaturation(factor, y, mn);
            c = max(y + (c - y) * factor, 0.0);

            if (hslOn > 0.5) c = applyHsl(c);

            if (vignetteStops != 0.0) {
                float2 d = coord - vignetteCenter;
                float2 uv = float2(d.x * vignetteRotation.x + d.y * vignetteRotation.y,
                                   -d.x * vignetteRotation.y + d.y * vignetteRotation.x) / vignetteHalf;
                c *= exp2(vignetteStops * smoothstep(vignetteLow, vignetteHigh, vignetteDistance(uv)));
            }

            // The last pass rolls colours above white (its own result and what it leaves outside its mask) to white.
            if (outputLimit <= 1.0) {
                c = rollToWhite(c);
                if (max(s0.r, max(s0.g, s0.b)) > 1.0) {
                    float3 l0 = rollToWhite(float3(srgbToLinear(s0.r), srgbToLinear(s0.g), srgbToLinear(s0.b)));
                    s0 = float3(linearToSrgb(l0.r), linearToSrgb(l0.g), linearToSrgb(l0.b));
                }
            }
            c = clamp(c, 0.0, outputLimit);
            c = float3(linearToSrgb(c.r), linearToSrgb(c.g), linearToSrgb(c.b));
            c = mix(min(s0, linearToSrgb(outputLimit)), c, float(mask.eval(coord).r));
            return half4(half3(c * a), half(a));
        }
        """;

    private static readonly float[] HslCentersUniform = [.. HslBands.RelativeCenters, 360f];

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
        double pixelScale = 1, HazeMap? hazeMap = null)
    {
        using var imageShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var white = SKShader.CreateColor(SKColors.White);
        // Dehaze (globally or in a mask) needs the photo's haze map; compute it here unless the caller has it.
        bool dehaze = state.Adjustments.Dehaze != 0 || state.Masks.Any(m => m.IsActive && m.Adjustments.Dehaze != 0);
        if (dehaze)
            hazeMap ??= HazeMap.For(image);
        else
            hazeMap = null;
        using var hazeShader = hazeMap is null
            ? SKShader.CreateColor(SKColors.White)
            : hazeMap.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear),
                SKMatrix.CreateScale((float)image.Width / hazeMap.Width, (float)image.Height / hazeMap.Height));
        var haze = new HazeInput(hazeShader, hazeMap);
        // Local highlights / shadows need the photo's base-brightness map.
        bool localTone = LocalTone(state.Adjustments) || state.Masks.Any(m => m.IsActive && LocalTone(m.Adjustments));
        var toneMap = localTone ? ToneBaseMap.For(image) : null;
        using var toneShader = toneMap is null
            ? SKShader.CreateColor(SKColors.Black)
            : toneMap.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear),
                SKMatrix.CreateScale((float)image.Width / toneMap.Width, (float)image.Height / toneMap.Height));
        var tone = new ToneInput(toneShader, toneMap is not null);
        var headroomMap = Headroom.Of(image) is { } hr && hr.Width == image.Width && hr.Height == image.Height ? hr : null;
        using var headroomShader = headroomMap is null
            ? SKShader.CreateColor(SKColors.Black)
            : headroomMap.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var fineShader = headroomMap?.FineImage is { } fineImage
            ? fineImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling)
            : SKShader.CreateColor(SKColors.Black);
        var extra = new HeadroomInput(headroomShader, headroomMap?.Scale ?? 0f, fineShader, headroomMap?.Fine is not null,
            Headroom.Of(image)?.BaseCurve == true);
        var shading = Lens.LensVignetting.Of(image);
        var table = shading?.Table;
        using var tableShader = table is null ? SKShader.CreateColor(SKColors.White) : TableImage(table)
            .ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
        var lens = new LensInput(tableShader, table is not null, image.Width, image.Height, shading?.Matrix);
        var masks = state.Masks.Select(m => (Mask: m, Image: m.IsActive ? maskImage(m) : null)).Where(m => m.Image is not null).ToList();
        var frame = VignetteMath.Frame.From(state.Crop.Frame(image.Width, image.Height));
        // Soften taps are a whole number of full-resolution pixels apart (as on the CPU at export).
        int fullLongSide = (int)Math.Round(Math.Max(image.Width, image.Height) / pixelScale);
        float softenStep = (float)(PreparedAdjustments.SoftenStepFor(fullLongSide) * pixelScale);
        var current = CreatePass(imageShader, imageShader, white, haze, tone, extra, lens, state.Adjustments, frame, (float)pixelScale, softenStep,
            sharpen: true, last: masks.Count == 0);

        for (int i = 0; i < masks.Count; i++)
        {
            var (mask, maskImg) = (masks[i].Mask, masks[i].Image!);
            var toImage = SKMatrix.CreateScale((float)image.Width / maskImg.Width, (float)image.Height / maskImg.Height);
            using var maskShader = maskImg.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                MaskSampling(maskImg, image), toImage);
            var next = CreatePass(current, imageShader, maskShader, haze, tone, extra, lens, mask.Adjustments, frame, (float)pixelScale, softenStep,
                sharpen: false, last: i == masks.Count - 1);
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

    private readonly record struct HazeInput(SKShader Shader, HazeMap? Map);

    private readonly record struct ToneInput(SKShader Shader, bool Bound);

    /// <summary>The drawn image's vignetting table (as a shader; Bound = false: none) and its size.</summary>
    private readonly record struct LensInput(SKShader Table, bool Bound, int Width, int Height, float[]? Matrix);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<float[], SKImage> TableImages = new();

    /// <summary>A vignetting table as a 256 × 1 half-float image (made once per table).</summary>
    private static SKImage TableImage(float[] table) => TableImages.GetValue(table, t =>
    {
        var halves = new Half[t.Length * 4];
        for (int i = 0; i < t.Length; i++)
        {
            halves[i * 4] = halves[i * 4 + 1] = halves[i * 4 + 2] = (Half)t[i];
            halves[i * 4 + 3] = (Half)1f;
        }
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
        using var data = SKData.CreateCopy(bytes);
        return SKImage.FromPixels(new SKImageInfo(t.Length, 1, SKColorType.RgbaF16, SKAlphaType.Opaque), data, t.Length * 8)
            ?? throw new InvalidOperationException("Could not create the vignetting table image.");
    });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<float[], SKImage> CurveImages = new();

    /// <summary>A tone curve table (<see cref="ToneCurveTable"/>) as a 1024 × 1 half-float RGB image (made once per table).</summary>
    private static SKImage CurveImage(float[] table) => CurveImages.GetValue(table, t =>
    {
        int n = t.Length / 3;
        var halves = new Half[n * 4];
        for (int i = 0; i < n; i++)
        {
            halves[i * 4] = (Half)t[i * 3];
            halves[i * 4 + 1] = (Half)t[i * 3 + 1];
            halves[i * 4 + 2] = (Half)t[i * 3 + 2];
            halves[i * 4 + 3] = (Half)1f;
        }
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
        using var data = SKData.CreateCopy(bytes);
        return SKImage.FromPixels(new SKImageInfo(n, 1, SKColorType.RgbaF16, SKAlphaType.Opaque), data, n * 8)
            ?? throw new InvalidOperationException("Could not create the tone curve image.");
    });

    private readonly record struct HeadroomInput(SKShader Shader, float Scale, SKShader Fine, bool HasFine, bool BaseCurve);

    /// <summary>Brightest linear value handed from one pass to the next (≈ 6 stops above white).</summary>
    public const float PassLimit = 64f;

    private static bool LocalTone(AdjustmentSettings s) => s.Highlights != 0 || s.Shadows != 0 || s.Clarity != 0;

    private static SKShader CreatePass(SKShader input, SKShader source, SKShader mask, HazeInput haze, ToneInput tone, HeadroomInput headroom, LensInput lens,
        AdjustmentSettings settings, VignetteMath.Frame frame, float pixelScale, float softenStep, bool sharpen, bool last)
    {
        var effect = Effect;
        var p = PreparedAdjustments.From(settings);
        // The tone curve is whole-image only (the global pass).
        var curve = sharpen ? p.CurveTable : null;
        using var curveShader = curve is null ? SKShader.CreateColor(SKColors.White)
            : CurveImage(curve).ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["exposureGain"] = p.ExposureGain,
            ["sceneExposure"] = headroom.BaseCurve ? 1f : 0f,
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
            ["hslOn"] = p.HasHsl ? 1f : 0f,
            ["hslCenters"] = HslCentersUniform,
            ["hslOrigin"] = HslBands.Centers[0],
            ["vignetteStops"] = p.VignetteStops,
            ["vignetteLow"] = p.VignetteLow,
            ["vignetteHigh"] = p.VignetteHigh,
            ["vignetteRoundness"] = p.VignetteRoundness,
            ["vignetteCenter"] = new[] { frame.CenterX, frame.CenterY },
            ["vignetteHalf"] = new[] { frame.HalfWidth, frame.HalfHeight },
            ["vignetteRotation"] = new[] { frame.Cos, frame.Sin },
            ["sharpenAmount"] = sharpen ? p.SharpenAmount : 0f,
            // On a reduced preview the radius shrinks with it, until the blur would be finer than the preview's own pixels and the
            // sharpening vanishes: keep at least half a preview pixel, so a photo shown small stays crisp (Lightroom sharpens at full size and then reduces).
            ["sharpenStep"] = pixelScale < 1 ? MathF.Max(p.SharpenRadius * pixelScale, 0.5f) : p.SharpenRadius * pixelScale,
            ["sharpenMasking"] = p.SharpenMasking,
            ["softenAmount"] = p.SoftenAmount,
            ["useToneBase"] = tone.Bound ? 1f : 0f,
            ["noiseLumaAmount"] = sharpen ? p.NoiseLuminanceAmount : 0f,
            ["noiseColorAmount"] = sharpen ? p.NoiseColorAmount : 0f,
            ["defringePurple"] = sharpen ? p.DefringePurpleAmount : 0f,
            ["defringeGreen"] = sharpen ? p.DefringeGreenAmount : 0f,
            ["purpleHues"] = new[] { p.PurpleHueFrom, p.PurpleHueTo },
            ["greenHues"] = new[] { p.GreenHueFrom, p.GreenHueTo },
            ["lumaNoiseStep"] = PreparedAdjustments.LumaNoiseStep * pixelScale,
            ["colorNoiseStep"] = PreparedAdjustments.ColorNoiseStep * pixelScale,
            ["fringeStep"] = PreparedAdjustments.FringeStep * pixelScale,
            ["hazeLight"] = haze.Map is { } m ? new[] { m.LightR, m.LightG, m.LightB } : new[] { 1f, 1f, 1f },
            ["dehazeAmount"] = haze.Map is null ? 0f : p.DehazeAmount,
            ["softenStep"] = softenStep,
            ["textureAmount"] = p.TextureAmount,
            ["textureStep"] = pixelScale,
            ["clarityAmount"] = p.ClarityAmount,
            ["curveOn"] = curve is null ? 0f : 1f,
            ["headroomScale"] = headroom.Scale,
            ["addHeadroom"] = sharpen && headroom.Scale > 0 ? 1f : 0f,
            ["addFine"] = sharpen && headroom.HasFine ? 1f : 0f,
            ["outputLimit"] = last ? 1f : PassLimit,
            // Lens corrections are whole-image: the global pass only.
            ["lensTableOn"] = lens.Bound ? 1f : 0f,
            ["lensVignetteManual"] = sharpen ? p.LensVignettingAmount : 0f,
            ["lensVignetteOn"] = sharpen && (lens.Bound || p.LensVignettingAmount != 0f) ? 1f : 0f,
            ["lensCenter"] = new[] { lens.Width / 2f, lens.Height / 2f },
            ["lensInvHalfDiagonal2"] = 4f / ((float)lens.Width * lens.Width + (float)lens.Height * lens.Height),
            ["lensRow0"] = lens.Matrix is { } m0 ? new[] { m0[0], m0[1], m0[2] } : new[] { 1f, 0f, 0f },
            ["lensRow1"] = lens.Matrix is { } m1 ? new[] { m1[3], m1[4], m1[5] } : new[] { 0f, 1f, 0f },
            ["lensRow2"] = lens.Matrix is { } m2 ? new[] { m2[6], m2[7], m2[8] } : new[] { 0f, 0f, 1f },
            ["lensMapOn"] = lens.Matrix is null ? 0f : 1f,
        };
        var children = new SKRuntimeEffectChildren(effect)
        {
            ["image"] = input,
            ["source"] = source,
            ["haze"] = haze.Shader,
            ["toneBase"] = tone.Shader,
            ["mask"] = mask,
            ["headroom"] = headroom.Shader,
            ["lensTable"] = lens.Table,
            ["fine"] = headroom.Fine,
            ["curveTable"] = curveShader,
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
    /// <remarks>
    /// Lens corrections and spot removal are applied to <paramref name="source"/> first, as
    /// <see cref="CpuAdjustmentRenderer.Render(SKBitmap, EditState, Lens.PhotoLens?)"/>
    /// does (the app shows a corrected, retouched image).
    /// </remarks>
    public static SKBitmap RenderRaster(SKBitmap source, EditState state, Lens.PhotoLens? lens = null)
    {
        var correction = Lens.LensSetup.For(lens ?? Lens.PhotoLens.Unknown, state.Adjustments, source.Width, source.Height,
            measureCa: () => Lens.ChromaticAberration.Measure(source));
        var corrected = correction?.Apply(source) ?? source;
        var retouched = Retouch.Retouching.Apply(corrected, state.Spots);
        try
        {
            return RenderPrepared(retouched, state, correction?.Shading);
        }
        finally
        {
            if (!ReferenceEquals(retouched, corrected))
                retouched.Dispose();
            if (!ReferenceEquals(corrected, source))
                corrected.Dispose();
        }
    }

    private static SKBitmap RenderPrepared(SKBitmap source, EditState state, Lens.LensShading? lensVignetting)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(source);
        Headroom.Attach(image, Headroom.Of(source));
        Lens.LensVignetting.Attach(image, lensVignetting);
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
