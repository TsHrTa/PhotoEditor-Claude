namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// CPU version of the shader's <c>softDetail</c>: the fine detail of the original photo (pixel minus an
/// edge-preserving 7 × 7 bilateral blur, spatial sigma 1.5 taps, range sigma on sRGB luminance). Soften subtracts
/// amount × detail. Taps are whole pixels apart and clamped at the border, like the shader at export scale.
/// </summary>
public readonly unsafe struct Softening
{
    private const int LutSize = 4096;

    /// <summary>Spatial weights of the 49 taps (row-major, j = -3..3, i = -3..3).</summary>
    private static readonly float[] Spatial = CreateSpatial();

    /// <summary>Range weight exp(-d² / 2σ²) for d² in [0, 1] (luminance differences are at most 1).</summary>
    private static readonly float[] Range = CreateRange();

    private readonly byte* _pixels;
    private readonly int _rowBytes, _width, _height, _step;

    /// <param name="pixels">The original photo, RGBA8888 premultiplied.</param>
    public Softening(nint pixels, int rowBytes, int width, int height, int step)
    {
        _pixels = (byte*)pixels;
        _rowBytes = rowBytes;
        _width = width;
        _height = height;
        _step = step;
    }

    /// <summary>Detail (unpremultiplied sRGB 0..1 units) of the original at pixel (x, y).</summary>
    public void Detail(int x, int y, out float dr, out float dg, out float db)
    {
        byte* c0 = _pixels + (long)y * _rowBytes + x * 4;
        if (c0[3] == 0)
        {
            dr = dg = db = 0;
            return;
        }
        float inv0 = 1f / c0[3];
        float cr = c0[0] * inv0, cg = c0[1] * inv0, cb = c0[2] * inv0;
        float yc = 0.2126f * cr + 0.7152f * cg + 0.0722f * cb;
        float sr = 0, sg = 0, sb = 0, wsum = 0;
        int k = 0;
        for (int j = -3; j <= 3; j++)
        {
            byte* row = _pixels + (long)Math.Clamp(y + j * _step, 0, _height - 1) * _rowBytes;
            for (int i = -3; i <= 3; i++, k++)
            {
                byte* t = row + Math.Clamp(x + i * _step, 0, _width - 1) * 4;
                if (t[3] == 0)
                    continue;
                float inv = 1f / t[3];
                float r = t[0] * inv, g = t[1] * inv, b = t[2] * inv;
                float d = 0.2126f * r + 0.7152f * g + 0.0722f * b - yc;
                float w = Spatial[k] * Range[(int)(MathF.Min(d * d, 1f) * (LutSize - 1) + 0.5f)];
                sr += w * r;
                sg += w * g;
                sb += w * b;
                wsum += w;
            }
        }
        dr = cr - sr / wsum;
        dg = cg - sg / wsum;
        db = cb - sb / wsum;
    }

    private static float[] CreateSpatial()
    {
        var w = new float[49];
        int k = 0;
        for (int j = -3; j <= 3; j++)
            for (int i = -3; i <= 3; i++)
                w[k++] = MathF.Exp(-(i * i + j * j) / 4.5f);
        return w;
    }

    private static float[] CreateRange()
    {
        var w = new float[LutSize];
        float s2 = 2f * PreparedAdjustments.SoftenRangeSigma * PreparedAdjustments.SoftenRangeSigma;
        for (int n = 0; n < LutSize; n++)
            w[n] = MathF.Exp(-(n / (float)(LutSize - 1)) / s2);
        return w;
    }
}
