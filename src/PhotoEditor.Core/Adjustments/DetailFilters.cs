namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// CPU versions of the shader's noise reduction and defringe (global pass): changes computed from the original
/// photo at whole-pixel tap spacings (as the shader at export scale), edge-clamped. Values are unpremultiplied sRGB 0..1.
/// </summary>
public readonly unsafe struct DetailFilters
{
    private readonly byte* _pixels;
    private readonly int _rowBytes, _width, _height;

    /// <param name="pixels">The original photo, RGBA8888 premultiplied.</param>
    public DetailFilters(nint pixels, int rowBytes, int width, int height)
    {
        _pixels = (byte*)pixels;
        _rowBytes = rowBytes;
        _width = width;
        _height = height;
    }

    private static float Lum(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    /// <summary>The original at (x, y), unpremultiplied (transparent pixels count as the fallback colour).</summary>
    private void At(int x, int y, float fr, float fg, float fb, out float r, out float g, out float b)
    {
        byte* p = _pixels + (long)Math.Clamp(y, 0, _height - 1) * _rowBytes + Math.Clamp(x, 0, _width - 1) * 4;
        if (p[3] == 0)
        {
            r = fr; g = fg; b = fb;
            return;
        }
        float inv = 1f / p[3];
        r = p[0] * inv;
        g = p[1] * inv;
        b = p[2] * inv;
    }

    /// <summary>The original's colour at (x, y) (the centre of the filters).</summary>
    public void Centre(int x, int y, float fr, float fg, float fb, out float r, out float g, out float b) =>
        At(x, y, fr, fg, fb, out r, out g, out b);

    /// <summary>Brightness minus its edge-preserving 5 × 5 average (spatial sigma 1 tap, range sigma 0.05).</summary>
    public float LumaNoise(int x, int y, float cr, float cg, float cb)
    {
        const int step = PreparedAdjustments.LumaNoiseStep;
        float y0 = Lum(cr, cg, cb), sum = 0, wsum = 0;
        for (int j = -2; j <= 2; j++)
            for (int i = -2; i <= 2; i++)
            {
                At(x + i * step, y + j * step, cr, cg, cb, out float r, out float g, out float b);
                float yy = Lum(r, g, b), d = yy - y0;
                float w = MathF.Exp(-(i * i + j * j) / 2f) * MathF.Exp(-d * d / 0.005f);
                sum += w * yy;
                wsum += w;
            }
        return y0 - sum / wsum;
    }

    /// <summary>Chroma minus its 5 × 5 average (spatial sigma 1.5 taps), guarded by brightness edges (range sigma 0.1).</summary>
    public void ColorNoise(int x, int y, float cr, float cg, float cb, out float nr, out float ng, out float nb)
    {
        const int step = PreparedAdjustments.ColorNoiseStep;
        float y0 = Lum(cr, cg, cb), sr = 0, sg = 0, sb = 0, wsum = 0;
        for (int j = -2; j <= 2; j++)
            for (int i = -2; i <= 2; i++)
            {
                At(x + i * step, y + j * step, cr, cg, cb, out float r, out float g, out float b);
                float yy = Lum(r, g, b), d = yy - y0;
                float w = MathF.Exp(-(i * i + j * j) / 4.5f) * MathF.Exp(-d * d / 0.02f);
                sr += w * (r - yy);
                sg += w * (g - yy);
                sb += w * (b - yy);
                wsum += w;
            }
        nr = cr - y0 - sr / wsum;
        ng = cg - y0 - sg / wsum;
        nb = cb - y0 - sb / wsum;
    }

    /// <summary>
    /// The change that desaturates a purple / green pixel near a brightness edge: the largest difference to the
    /// 8 neighbours at <see cref="PreparedAdjustments.FringeStep"/> and at 3 × that (fringes are several pixels wide).
    /// </summary>
    public void Defringe(int x, int y, float cr, float cg, float cb, in PreparedAdjustments p,
        out float dr, out float dg, out float db)
    {
        const int step = PreparedAdjustments.FringeStep;
        float y0 = Lum(cr, cg, cb), edgeDiff = 0;
        for (int ring = 1; ring <= 3; ring += 2)
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    At(x + i * step * ring, y + j * step * ring, cr, cg, cb, out float r, out float g, out float b);
                    edgeDiff = MathF.Max(edgeDiff, MathF.Abs(Lum(r, g, b) - y0));
                }
        float edge = ToneCurve.SmoothStep(PreparedAdjustments.FringeEdgeLow, PreparedAdjustments.FringeEdgeHigh, edgeDiff);
        float h = Hue(cr, cg, cb);
        float purple = PreparedAdjustments.HueRangeWeight(h, p.PurpleHueFrom, p.PurpleHueTo);
        float green = PreparedAdjustments.HueRangeWeight(h, p.GreenHueFrom, p.GreenHueTo);
        float k = Math.Clamp((p.DefringePurpleAmount * purple + p.DefringeGreenAmount * green) * edge
            * PreparedAdjustments.DefringeStrength, 0f, 1f);
        dr = -k * (cr - y0);
        dg = -k * (cg - y0);
        db = -k * (cb - y0);
    }

    /// <summary>HSV hue in degrees, exactly as the shader's rgbToHsv.</summary>
    private static float Hue(float r, float g, float b)
    {
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b)), d = mx - mn;
        if (d <= 0f)
            return 0f;
        float h = mx == r ? 60f * ((g - b) / d) : mx == g ? 60f * ((b - r) / d + 2f) : 60f * ((r - g) / d + 4f);
        return h < 0f ? h + 360f : h;
    }
}
