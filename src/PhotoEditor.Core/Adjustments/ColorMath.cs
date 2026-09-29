namespace PhotoEditor.Core.Adjustments;

/// <summary>Colour helper functions shared by the CPU pipeline. Mirrored in <see cref="AdjustmentShader"/>.</summary>
public static class ColorMath
{
    private static readonly float[] SrgbToLinearTable = BuildSrgbTable();

    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    /// <summary>Fast sRGB decode of an 8-bit channel value.</summary>
    public static float SrgbByteToLinear(byte b) => SrgbToLinearTable[b];

    public static byte ToByte(float c) => (byte)Math.Clamp((int)MathF.Round(c * 255f), 0, 255);

    private static float[] BuildSrgbTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
            t[i] = SrgbToLinear(i / 255f);
        return t;
    }
}
