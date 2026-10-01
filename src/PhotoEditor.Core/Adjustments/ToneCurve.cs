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

    /// <summary>
    /// The global part of the tone curve: contrast, whites and blacks. Highlights / shadows are applied locally
    /// (<see cref="LocalGain"/>) on the photo's base brightness.
    /// </summary>
    public static float ApplyGlobal(float x, in PreparedAdjustments p)
    {
        x = Contrast(x, p.ContrastGamma);
        x = Whites(Whites(x, p.WhitesAmount), p.WhitesAmount2);
        x = Blacks(Blacks(x, p.BlacksAmount), p.BlacksAmount2);
        return MathF.Max(x, 0f);
    }

    /// <summary>Largest brightening / darkening (factor) the local highlights / shadows may apply to a pixel.</summary>
    public const float MaxLocalGain = 8f;

    /// <summary>
    /// Highlights / shadows as a brightness factor for a pixel whose base (area) luminance is
    /// <paramref name="baseLuminance"/> (linear): the curve moves the base, the pixel follows by the same factor,
    /// so its detail relative to the area is kept. Mirrored in the shader.
    /// </summary>
    public static float LocalGain(float baseLuminance, in PreparedAdjustments p)
    {
        float yb = MathF.Max(baseLuminance, 1e-6f);
        float x = MathF.Pow(yb, 1f / PerceptualGamma);
        x = Highlights(Highlights(x, p.HighlightsAmount), p.HighlightsAmount2);
        x = Shadows(Shadows(x, p.ShadowsAmount), p.ShadowsAmount2);
        float y2 = MathF.Pow(MathF.Max(x, 0f), PerceptualGamma);
        return Math.Clamp(y2 / yb, 1f / MaxLocalGain, MaxLocalGain);
    }

    /// <summary>
    /// Clarity as a brightness factor: the pixel's contrast against its area (pixel / base, in stops) is scaled by
    /// 1 + <paramref name="amount"/> × a midtone weight of the base, i.e. factor = baseRatio^(−amount · weight) with
    /// baseRatio = base / pixel. Flat areas and pixels on an edge the base follows get ≈ 1 (no halos). Mirrored in the shader.
    /// </summary>
    public static float ClarityGain(float baseLuminance, float baseRatio, float amount)
    {
        float x = MathF.Pow(MathF.Max(baseLuminance, 0f), 1f / PerceptualGamma);
        float weight = Math.Clamp(4f * x * (1f - x), 0f, 1f);
        float gain = MathF.Pow(MathF.Max(baseRatio, 1e-6f), -amount * weight);
        return Math.Clamp(gain, 1f / PreparedAdjustments.MaxClarityGain, PreparedAdjustments.MaxClarityGain);
    }

    /// <summary>Where the highlights slider starts to act (perceptual; ≈ 13 % grey): midtones below stay put.</summary>
    public const float HighlightsPivot = 0.4f;

    /// <summary>Steepness of the positive highlights curve per unit of amount (γ = 1 + this × amount; 4 at +100).</summary>
    public const float HighlightsLiftGamma = 12f;

    /// <summary>
    /// The highlights curve on t = position between <see cref="HighlightsPivot"/> and white (midtones below stay put).
    /// <para>
    /// Pulled down: lowers white by <paramref name="amount"/>, easing in (t²(2 − t), so the slope never drops below
    /// ≈ 0.45 at −100 and the bright areas keep their contrast instead of turning a flat grey). Above white (RAW
    /// highlights) the curve continues with its slope at white, rolled off, d / (1 + k·d) with k = 10 × |amount|
    /// (2.5 at −100), so clouds 2 stops over come back under white.
    /// </para>
    /// <para>
    /// Pushed up: brightens the upper tones but keeps white at white, as Lightroom does (Whites moves the white
    /// point): t + t·(h − t) with h = 1 − (1 − t)^γ, rising everywhere, slope 1 at the pivot and 0 at white. Values
    /// above white are left alone.
    /// </para>
    /// Mirrored in the shader.
    /// </summary>
    public static float Highlights(float x, float amount)
    {
        if (x <= HighlightsPivot || (amount > 0f && x >= 1f))
            return x;
        if (x < 1f)
        {
            float t = (x - HighlightsPivot) / (1f - HighlightsPivot);
            if (amount < 0f)
                return x + amount * t * t * (2f - t);
            float h = 1f - MathF.Pow(1f - t, 1f + HighlightsLiftGamma * amount);
            return HighlightsPivot + (t + t * (h - t)) * (1f - HighlightsPivot);
        }
        float d = x - 1f, slope = 1f + amount / (1f - HighlightsPivot);
        return 1f + amount + slope * d / (1f - 10f * amount * d);
    }

    /// <summary>Linear value from which a brightened pixel's brightest channel is compressed towards white.</summary>
    public const float BrightenKnee = 0.6f;

    /// <summary>
    /// Limits a brightening <paramref name="gain"/> (&gt; 1) for a pixel whose brightest channel is
    /// <paramref name="hi"/> (linear), so that channel approaches white softly (exponential shoulder above
    /// <see cref="BrightenKnee"/>) instead of clipping: all channels keep their ratios, so the colour stays the
    /// same instead of shifting (yellow → green) or bleaching as it would when one channel clips. Never darkens.
    /// Mirrored in the shader.
    /// </summary>
    public static float LimitBrightening(float hi, float gain)
    {
        float v = hi * gain;
        if (gain <= 1f || hi <= 0f || v <= BrightenKnee)
            return gain;
        float s = BrightenKnee + (1f - BrightenKnee) * (1f - MathF.Exp(-(v - BrightenKnee) / (1f - BrightenKnee)));
        return MathF.Max(s / hi, 1f);
    }

    /// <summary>
    /// How fast colours above white fade to white (<see cref="RollToWhite"/>): at this much over white (linear) the
    /// colour is halfway to white.
    /// </summary>
    public const float WhiteRolloff = 1f;

    /// <summary>
    /// Before the final clip: a pixel whose brightest channel is above white has its other channels raised towards
    /// it, by e² / (e² + 1) with e = (hi − 1) / <see cref="WhiteRolloff"/> (gentle just over white), so overexposed colours burn out to white as on film
    /// and in Lightroom, instead of clipping to a saturated colour. No change at or below white. Mirrored in the shader.
    /// </summary>
    public static void RollToWhite(ref float r, ref float g, ref float b)
    {
        float hi = MathF.Max(r, MathF.Max(g, b));
        if (hi <= 1f)
            return;
        float e = (hi - 1f) / WhiteRolloff, s = e * e / (e * e + 1f);
        r += (hi - r) * s;
        g += (hi - g) * s;
        b += (hi - b) * s;
    }

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
