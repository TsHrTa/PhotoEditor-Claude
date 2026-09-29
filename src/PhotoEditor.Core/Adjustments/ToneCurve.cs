namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Tone math on a perceptual value <c>x</c> (luminance^(1/2.2); 0 = black, 1 = white, may exceed 1).
/// Mirrored line by line in <see cref="AdjustmentShader"/>.
/// </summary>
public static class ToneCurve
{
    public const float PerceptualGamma = 2.2f;

    public static float Apply(float x, in PreparedAdjustments p)
    {
        x = Contrast(x, p.ContrastGamma);
        // Each step runs twice: the second pass carries the part of the slider beyond ±100 (0 otherwise).
        x = Highlights(Highlights(x, p.HighlightsAmount), p.HighlightsAmount2);
        x = Shadows(Shadows(x, p.ShadowsAmount), p.ShadowsAmount2);
        x = Whites(Whites(x, p.WhitesAmount), p.WhitesAmount2);
        x = Blacks(Blacks(x, p.BlacksAmount), p.BlacksAmount2);
        return MathF.Max(x, 0f);
    }

    /// <summary>Shifts the upper range, fully at and above white (recovers over-exposure).</summary>
    public static float Highlights(float x, float amount) => x + amount * SmoothStep(0.35f, 1f, x);

    /// <summary>Bump peaking at x = 1/3, zero at black and white.</summary>
    public static float Shadows(float x, float amount)
    {
        float xs = Math.Clamp(x, 0f, 1f);
        return x + amount * 6.75f * xs * (1f - xs) * (1f - xs);
    }

    /// <summary>Moves the white end of the range, little effect on midtones.</summary>
    public static float Whites(float x, float amount)
    {
        float xs = Math.Clamp(x, 0f, 1f);
        return x + amount * xs * xs * xs * xs;
    }

    /// <summary>Moves the black end of the range, little effect on midtones.</summary>
    public static float Blacks(float x, float amount)
    {
        float inv = 1f - Math.Clamp(x, 0f, 1f);
        return x + amount * inv * inv * inv * inv;
    }

    /// <summary>S-curve pivoting at 0.5; gamma &gt; 1 adds contrast, &lt; 1 removes it. Identity outside [0,1].</summary>
    public static float Contrast(float x, float gamma)
    {
        if (x <= 0f || x >= 1f)
            return x;
        return x < 0.5f
            ? 0.5f * MathF.Pow(2f * x, gamma)
            : 1f - 0.5f * MathF.Pow(2f - 2f * x, gamma);
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
}
