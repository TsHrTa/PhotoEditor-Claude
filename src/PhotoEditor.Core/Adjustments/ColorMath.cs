namespace PhotoEditor.Core.Adjustments;

/// <summary>Colour helper functions shared by the CPU pipeline. Mirrored in <see cref="AdjustmentShader"/>.</summary>
public static class ColorMath
{
    private static readonly float[] SrgbToLinearTable = BuildSrgbTable();

    /// <summary>Slope of the sRGB decode at each 8-bit value (per unit of the 0..1 value).</summary>
    private static readonly float[] SrgbSlopeTable = BuildSlopeTable();

    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    /// <summary>Fast sRGB decode of an 8-bit channel value.</summary>
    public static float SrgbByteToLinear(byte b) => SrgbToLinearTable[b];

    /// <summary>
    /// sRGB decode of an 8-bit value plus a fraction of a step (<paramref name="offset"/>, at most ±0.5 / 255):
    /// the table value moved along the curve's slope (error ≪ 1e-5).
    /// </summary>
    public static float SrgbByteToLinear(byte b, float offset) => SrgbToLinearTable[b] + SrgbSlopeTable[b] * offset;

    private const int EncodeSteps = 16384;
    private static readonly float[] EncodeTable = BuildEncodeTable();

    private static float[] BuildEncodeTable()
    {
        var table = new float[EncodeSteps + 2];
        for (int i = 0; i < table.Length; i++)
            table[i] = LinearToSrgb((float)i / EncodeSteps);
        return table;
    }

    /// <summary>
    /// <see cref="LinearToSrgb"/> interpolated from a table below white (error &lt; 1e-5, under the RAW fine layer's
    /// step); exact above it. For per-pixel work on whole images.
    /// </summary>
    public static float LinearToSrgbFast(float linear)
    {
        if (linear >= 1f)
            return LinearToSrgb(linear);
        if (linear <= 0f)
            return 0f;
        float at = linear * EncodeSteps;
        int i = (int)at;
        return EncodeTable[i] + (EncodeTable[i + 1] - EncodeTable[i]) * (at - i);
    }

    public static byte ToByte(float c) => (byte)Math.Clamp((int)MathF.Round(c * 255f), 0, 255);

    private static float[] BuildSlopeTable()
    {
        var t = new float[256];
        const float h = 0.25f / 255f;
        for (int i = 0; i < 256; i++)
            t[i] = (SrgbToLinear(i / 255f + h) - SrgbToLinear(Math.Max(i / 255f - h, 0f))) / (i == 0 ? h : 2 * h);
        return t;
    }

    private static float[] BuildSrgbTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
            t[i] = SrgbToLinear(i / 255f);
        return t;
    }
}
