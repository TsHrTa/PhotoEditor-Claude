using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
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
    /// <param name="lens">The photo's lens information, for the lens corrections (null = unknown).</param>
    public static SKBitmap Render(SKBitmap source, EditState state, Lens.PhotoLens? lens = null)
    {
        // Lens corrections, then spot removal, work on the photo's pixels, before everything else.
        var correction = Lens.LensSetup.For(lens ?? Lens.PhotoLens.Unknown, state.Adjustments, source.Width, source.Height,
            measureCa: () => Lens.ChromaticAberration.Measure(source));
        var corrected = correction?.Apply(source) ?? source;
        try
        {
            var retouched = Retouch.Retouching.Apply(corrected, state.Spots);
            try
            {
                return RenderCorrected(retouched, state, correction?.Shading);
            }
            finally
            {
                if (!ReferenceEquals(retouched, corrected))
                    retouched.Dispose();
            }
        }
        finally
        {
            if (!ReferenceEquals(corrected, source))
                corrected.Dispose();
        }
    }

    /// <summary>Renders a photo whose lens corrections (except the vignetting, <paramref name="lensVignetting"/>) and spots are done.</summary>
    private static SKBitmap RenderCorrected(SKBitmap source, EditState state, Lens.LensShading? lensVignetting)
    {
        using var src = source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Premul
            ? null
            : source.Copy(SKColorType.Rgba8888);
        var input = src ?? source;
        if (input.AlphaType != SKAlphaType.Premul && input.AlphaType != SKAlphaType.Opaque)
            throw new NotSupportedException($"Unsupported alpha type {input.AlphaType}");
        var p = PreparedAdjustments.From(state.Adjustments);
        var original = input;
        // RAW highlights above white, added after the detail filters (as the shader's global pass does).
        var headroom = Headroom.Of(source) is { } hr && hr.Width == input.Width && hr.Height == input.Height ? hr : null;
        // Sharpening works on the source pixels, before everything else (as the shader's global pass does).
        using var sharpened = p.HasSharpening ? Sharpening.Apply(input, p) : null;
        input = sharpened ?? input;

        int width = input.Width, height = input.Height;
        var lensGain = new LensGain(lensVignetting, (float)Math.Clamp(state.Adjustments.LensVignetting, -100, 100) / 100f, width, height);
        var layers = state.Masks
            .Where(m => m.IsActive)
            // The tone curve is whole-image only.
            .Select(m => new MaskLayer(PreparedAdjustments.From(m.Adjustments) with { CurveTable = null }, MaskRasterizer.RasterizeToBytes(m, width, height)))
            .ToArray();
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var frame = VignetteMath.Frame.From(state.Crop.Frame(width, height));
        int rowBytesIn = input.RowBytes, rowBytesOut = result.RowBytes;
        nint inPtr = input.GetPixels(), outPtr = result.GetPixels();
        nint extraPtr = headroom?.Bitmap.GetPixels() ?? 0;
        int extraRowBytes = headroom?.Bitmap.RowBytes ?? 0;
        nint finePtr = headroom?.Fine?.GetPixels() ?? 0;
        int fineRowBytes = headroom?.Fine?.RowBytes ?? 0;
        // Soften / Texture (global or in a mask) use the original's fine detail.
        // Dehaze (global or in a mask) uses the photo's haze map, computed from the original.
        var haze = p.HasDehaze || layers.Any(l => l.Adjustments.HasDehaze) ? HazeMap.Compute(original) : null;
        // Local highlights / shadows and clarity (global or in a mask) use the photo's base brightness map.
        var toneBase = p.NeedsToneBase || layers.Any(l => l.Adjustments.NeedsToneBase) ? ToneBaseMap.Compute(original, headroom) : null;
        var originalPixels = new ToneSource(original.GetPixels(), original.RowBytes, headroom);
        DetailFilters? detailFilters = p.HasNoiseReduction || p.HasDefringe
            ? new DetailFilters(original.GetPixels(), original.RowBytes, width, height)
            : null;
        Softening? soften = UsesDetail(p) || layers.Any(l => UsesDetail(l.Adjustments))
            ? new Softening(original.GetPixels(), original.RowBytes, width, height, PreparedAdjustments.SoftenStepFor(Math.Max(width, height)))
            : null;

        Parallel.For(0, height, y =>
        {
            unsafe
            {
                var inRow = new ReadOnlySpan<byte>((byte*)inPtr + (long)y * rowBytesIn, width * 4);
                var outRow = new Span<byte>((byte*)outPtr + (long)y * rowBytesOut, width * 4);
                var extraRow = headroom is null ? default : new ReadOnlySpan<byte>((byte*)extraPtr + (long)y * extraRowBytes, width * 4);
                var fineRow = finePtr == 0 ? default : new ReadOnlySpan<byte>((byte*)finePtr + (long)y * fineRowBytes, width * 4);
                ProcessRow(inRow, outRow, p, layers, y, frame, soften, haze, height, detailFilters, toneBase, originalPixels,
                    extraRow, headroom?.Scale ?? 0f, fineRow, lensGain);
            }
        });
        return result;
    }

    /// <summary>A mask's prepared adjustments and its 8-bit coverage at output resolution.</summary>
    private sealed record MaskLayer(PreparedAdjustments Adjustments, byte[] Mask);

    /// <summary>Processes one row of premultiplied RGBA8888 pixels.</summary>
    public static void ProcessRow(ReadOnlySpan<byte> input, Span<byte> output, in PreparedAdjustments p) =>
        ProcessRow(input, output, p, [], 0, VignetteMath.Frame.Full(input.Length / 4, 1), null, null, 1, null, null, default, default, 0f, default,
            new LensGain(null, p.LensVignettingAmount, input.Length / 4, 1));

    private static void ProcessRow(ReadOnlySpan<byte> input, Span<byte> output, in PreparedAdjustments p, MaskLayer[] layers, int y,
        in VignetteMath.Frame frame, Softening? soften, HazeMap? haze, int height, DetailFilters? detailFilters,
        ToneBaseMap? toneBase, ToneSource originalPixels, ReadOnlySpan<byte> headroom, float headroomScale, ReadOnlySpan<byte> fine,
        in LensGain lensGain)
    {
        int width = input.Length / 4;
        // The global pass hands values up to PassLimit to the masks; the last pass clips at white.
        float globalLimit = layers.Length > 0 ? AdjustmentShader.PassLimit : 1f;
        for (int i = 0; i < input.Length; i += 4)
        {
            byte a8 = input[i + 3];
            if (a8 == 0)
            {
                output.Slice(i, 4).Clear();
                continue;
            }

            float r, g, b;
            // The original's fine detail at this pixel, computed once when a pass softens or adds texture.
            var detail = new PixelDetail(soften, i / 4, y);
            bool above = !headroom.IsEmpty && (headroom[i] | headroom[i + 1] | headroom[i + 2]) != 0;
            if ((UsesDetail(p) && soften is not null) || detailFilters is not null || above)
            {
                float inv = 1f / a8;
                float s0 = input[i] * inv, s1 = input[i + 1] * inv, s2 = input[i + 2] * inv;
                // Noise reduction and defringe: changes computed from the original (as the shader).
                if (detailFilters is { } df)
                {
                    int x = i / 4;
                    df.Centre(x, y, s0, s1, s2, out float cr, out float cg, out float cb);
                    if (p.NoiseLuminanceAmount > 0f)
                    {
                        float n = p.NoiseLuminanceAmount * df.LumaNoise(x, y, cr, cg, cb);
                        s0 -= n; s1 -= n; s2 -= n;
                    }
                    if (p.NoiseColorAmount > 0f)
                    {
                        df.ColorNoise(x, y, cr, cg, cb, out float nr, out float ng, out float nb);
                        s0 -= p.NoiseColorAmount * nr; s1 -= p.NoiseColorAmount * ng; s2 -= p.NoiseColorAmount * nb;
                    }
                    if (p.HasDefringe)
                    {
                        df.Defringe(x, y, cr, cg, cb, p, out float fr, out float fg, out float fb);
                        s0 += fr; s1 += fg; s2 += fb;
                    }
                    s0 = Math.Clamp(s0, 0f, 1f); s1 = Math.Clamp(s1, 0f, 1f); s2 = Math.Clamp(s2, 0f, 1f);
                }
                if (soften is not null)
                    SoftenAndTexture(ref detail, p, ref s0, ref s1, ref s2);
                if (above)
                {
                    s0 += headroom[i] * headroomScale / 255f;
                    s1 += headroom[i + 1] * headroomScale / 255f;
                    s2 += headroom[i + 2] * headroomScale / 255f;
                }
                if (!fine.IsEmpty)
                {
                    s0 += Headroom.FineOffset(fine[i]);
                    s1 += Headroom.FineOffset(fine[i + 1]);
                    s2 += Headroom.FineOffset(fine[i + 2]);
                }
                r = ColorMath.SrgbToLinear(s0);
                g = ColorMath.SrgbToLinear(s1);
                b = ColorMath.SrgbToLinear(s2);
            }
            else if (a8 == 255 && !fine.IsEmpty)
            {
                r = ColorMath.SrgbByteToLinear(input[i], Headroom.FineOffset(fine[i]));
                g = ColorMath.SrgbByteToLinear(input[i + 1], Headroom.FineOffset(fine[i + 1]));
                b = ColorMath.SrgbByteToLinear(input[i + 2], Headroom.FineOffset(fine[i + 2]));
            }
            else if (a8 == 255)
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

            // Lens vignetting (profile removed, manual applied), in linear light.
            if (lensGain.IsActive)
            {
                float gain = lensGain.At(i / 4, y);
                r *= gain;
                g *= gain;
                b *= gain;
            }

            // Transmission at this pixel, sampled once when a pass dehazes.
            float t = -1f;
            if (p.HasDehaze && haze is not null)
            {
                t = haze.Sample(i / 4, y, width, height);
                HazeMap.Apply(ref r, ref g, ref b, t, p.DehazeAmount, haze.LightR, haze.LightG, haze.LightB);
            }
            // Base / pixel brightness from the original, computed once per pixel when a pass needs it.
            float baseRatio = toneBase is not null ? originalPixels.BaseRatio(toneBase, i / 4, y, width, height) : 1f;
            ApplyLinear(ref r, ref g, ref b, p, baseRatio);
            if (p.HasVignette)
                VignetteMath.Apply(ref r, ref g, ref b, i / 4, y, frame, p);
            // Result of the global pass as unpremultiplied sRGB, like the shader hands to the next pass.
            float sr = ColorMath.LinearToSrgb(Math.Clamp(r, 0f, globalLimit));
            float sg = ColorMath.LinearToSrgb(Math.Clamp(g, 0f, globalLimit));
            float sb = ColorMath.LinearToSrgb(Math.Clamp(b, 0f, globalLimit));

            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                float limit = li == layers.Length - 1 ? 1f : AdjustmentShader.PassLimit;
                float m = layer.Mask[y * width + i / 4] / 255f;
                if (m <= 0f)
                    continue;
                float lr = sr, lg = sg, lb = sb;
                if (soften is not null)
                    SoftenAndTexture(ref detail, layer.Adjustments, ref lr, ref lg, ref lb);
                r = ColorMath.SrgbToLinear(lr);
                g = ColorMath.SrgbToLinear(lg);
                b = ColorMath.SrgbToLinear(lb);
                if (layer.Adjustments.HasDehaze && haze is not null)
                {
                    if (t < 0f)
                        t = haze.Sample(i / 4, y, width, height);
                    HazeMap.Apply(ref r, ref g, ref b, t, layer.Adjustments.DehazeAmount, haze.LightR, haze.LightG, haze.LightB);
                }
                ApplyLinear(ref r, ref g, ref b, layer.Adjustments, baseRatio);
                if (layer.Adjustments.HasVignette)
                    VignetteMath.Apply(ref r, ref g, ref b, i / 4, y, frame, layer.Adjustments);
                float limitEncoded = ColorMath.LinearToSrgb(limit);
                sr = Mix(MathF.Min(sr, limitEncoded), ColorMath.LinearToSrgb(Math.Clamp(r, 0f, limit)), m);
                sg = Mix(MathF.Min(sg, limitEncoded), ColorMath.LinearToSrgb(Math.Clamp(g, 0f, limit)), m);
                sb = Mix(MathF.Min(sb, limitEncoded), ColorMath.LinearToSrgb(Math.Clamp(b, 0f, limit)), m);
            }

            float a = a8 / 255f;
            output[i] = ColorMath.ToByte(sr * a);
            output[i + 1] = ColorMath.ToByte(sg * a);
            output[i + 2] = ColorMath.ToByte(sb * a);
            output[i + 3] = a8;
        }
    }

    private static bool UsesDetail(in PreparedAdjustments p) => p.HasSoften || p.HasTexture;

    /// <summary>
    /// Soften, then texture, on unpremultiplied sRGB values (as the shader); values stay ≥ 0 and are not pushed
    /// above white (nor clipped when they already are, in a mask pass).
    /// </summary>
    private static void SoftenAndTexture(ref PixelDetail detail, in PreparedAdjustments p, ref float s0, ref float s1, ref float s2)
    {
        if (p.HasSoften)
        {
            detail.Get(out float dr, out float dg, out float db);
            s0 = Math.Clamp(s0 - p.SoftenAmount * dr, 0f, MathF.Max(s0, 1f));
            s1 = Math.Clamp(s1 - p.SoftenAmount * dg, 0f, MathF.Max(s1, 1f));
            s2 = Math.Clamp(s2 - p.SoftenAmount * db, 0f, MathF.Max(s2, 1f));
        }
        if (p.HasTexture)
        {
            float t = p.TextureAmount * detail.Texture();
            s0 = Math.Clamp(s0 + t, 0f, MathF.Max(s0, 1f));
            s1 = Math.Clamp(s1 + t, 0f, MathF.Max(s1, 1f));
            s2 = Math.Clamp(s2 + t, 0f, MathF.Max(s2, 1f));
        }
    }

    /// <summary>The original's detail at one pixel, computed on first use and shared by all passes.</summary>
    private struct PixelDetail(Softening? soften, int x, int y)
    {
        private bool _hasDetail, _hasTexture;
        private float _dr, _dg, _db, _texture;

        /// <summary>Fine detail per channel: pixel − bilateral blur (Soften).</summary>
        public void Get(out float dr, out float dg, out float db)
        {
            if (!_hasDetail)
            {
                soften!.Value.Detail(x, y, out _dr, out _dg, out _db);
                _hasDetail = true;
            }
            (dr, dg, db) = (_dr, _dg, _db);
        }

        /// <summary>Medium-size detail on luminance: 3 × 3 blur − bilateral blur (Texture).</summary>
        public float Texture()
        {
            if (!_hasTexture)
            {
                Get(out float dr, out float dg, out float db);
                _texture = soften!.Value.SmallBlurMinusCenter(x, y) + ToneCurve.Luminance(dr, dg, db);
                _hasTexture = true;
            }
            return _texture;
        }
    }

    /// <summary>Lens vignetting of the whole image: r = 1 at the corner.</summary>
    private readonly struct LensGain(Lens.LensShading? shading, float manual, int width, int height)
    {
        private readonly float _invHalfDiagonal = 2f / MathF.Sqrt((float)width * width + (float)height * height);

        public bool IsActive => shading?.Table is not null || manual != 0f;

        public float At(int x, int y)
        {
            float nx = (x + 0.5f - width / 2f) * _invHalfDiagonal, ny = (y + 0.5f - height / 2f) * _invHalfDiagonal;
            return Lens.LensVignetting.Gain(shading?.Table, Lens.LensVignetting.R2(shading?.Matrix, nx, ny), manual, nx * nx + ny * ny);
        }
    }

    /// <summary>The original photo's pixels, for the base / pixel brightness ratio.</summary>
    private readonly unsafe struct ToneSource(nint pixels, int rowBytes, Headroom? headroom)
    {
        private readonly nint _extra = headroom?.Bitmap.GetPixels() ?? 0;
        private readonly int _extraRowBytes = headroom?.Bitmap.RowBytes ?? 0;
        private readonly float _extraScale = (headroom?.Scale ?? 0f) / 255f;

        public float BaseRatio(ToneBaseMap map, int x, int y, int width, int height)
        {
            byte* p = (byte*)pixels + (long)y * rowBytes + x * 4;
            float er = 0, eg = 0, eb = 0;
            if (_extra != 0)
            {
                byte* e = (byte*)_extra + (long)y * _extraRowBytes + x * 4;
                er = e[0] * _extraScale; eg = e[1] * _extraScale; eb = e[2] * _extraScale;
            }
            return map.BaseRatio(x, y, width, height, ToneBaseMap.LogLuminance(p[0], p[1], p[2], p[3], er, eg, eb));
        }
    }

    /// <summary>Same formula as SkSL <c>mix</c>.</summary>
    private static float Mix(float x, float y, float t) => x * (1f - t) + y * t;

    /// <summary>The per-pixel adjustment math on linear-light RGB.</summary>
    /// <param name="baseRatio">
    /// The photo's base (area) brightness over this pixel's brightness, from <see cref="ToneBaseMap"/> (1 = no map:
    /// highlights / shadows then act on the pixel itself, like a global curve).
    /// </param>
    public static void ApplyLinear(ref float r, ref float g, ref float b, in PreparedAdjustments p, float baseRatio = 1f)
    {
        r *= p.WhiteBalanceR * p.ExposureGain;
        g *= p.WhiteBalanceG * p.ExposureGain;
        b *= p.WhiteBalanceB * p.ExposureGain;

        // Highlights / shadows: local, a factor from the curve on the base brightness (keeps local detail).
        float y = ToneCurve.Luminance(r, g, b);
        if (p.HasLocalTone)
        {
            float gain = ToneCurve.LocalGain(y * baseRatio, p);
            r *= gain;
            g *= gain;
            b *= gain;
            y *= gain;
        }

        // Clarity: the pixel's contrast against its area, scaled (also local, on the same base).
        if (p.HasClarity)
        {
            float gain = ToneCurve.ClarityGain(y * baseRatio, baseRatio, p.ClarityAmount);
            r *= gain;
            g *= gain;
            b *= gain;
            y *= gain;
        }

        // Tone: contrast / whites / blacks curve on perceptual luminance, applied to RGB as a ratio (keeps hue).
        float yp = MathF.Pow(MathF.Max(y, 0f), 1f / ToneCurve.PerceptualGamma);
        float yp2 = ToneCurve.ApplyGlobal(yp, p);
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

        // Tone curve panel: per channel, on perceptual values (like Lightroom's curves, it also changes saturation).
        if (p.CurveTable is { } curve)
            ToneCurveTable.Apply(curve, ref r, ref g, ref b);

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
