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
    /// <param name="detailRatio">
    /// Local mean over pixel luminance (<see cref="ToneBaseMap.Ratios"/>, limited to ±1 stop; 1 = no map). Highlights
    /// acts on that plain local mean, not on the edge-preserving base: the guided base follows foliage texture pixel by
    /// pixel, so a darkening that grows with brightness flattened the texture (19 % less detail at −95, where Lightroom
    /// keeps it). A darkened area also gets back the detail the darkening takes from it, ×
    /// <see cref="HighlightsDetailKeep"/> (factor detailRatio^(keep · (1 − 1/g)) for the perceptual factor g), and on
    /// RAWs what the base curve's shoulder pressed together (<see cref="ShoulderLift"/>: clouds).
    /// Shadows stays on the edge-preserving base.
    /// </param>
    public static float LocalGain(float baseLuminance, in PreparedAdjustments p, float detailRatio = 1f, float meanLuminance = -1f)
    {
        float yb = MathF.Max(baseLuminance, 1e-6f);
        float gain = 1f;
        if (p.HighlightsAmount != 0f)
        {
            // the local mean (pixel × detailRatio; without a map the base)
            float ym = MathF.Max(meanLuminance >= 0f ? meanLuminance : yb, 1e-6f);
            float h0 = MathF.Pow(ym, 1f / PerceptualGamma);
            float h = Highlights(Highlights(h0, p.HighlightsAmount), p.HighlightsAmount2);
            gain = MathF.Pow(MathF.Max(h, 0f) / h0, PerceptualGamma);
            if (h < h0 && h > 0f)
            {
                float meanGain = gain;
                gain *= MathF.Pow(MathF.Max(detailRatio, 1e-6f), HighlightsDetailKeep * (1f - h0 / h));
                // at most back to where the pixel was
                if (p.SceneExposure)
                    gain *= MathF.Min(ShoulderLift(ym, meanGain, detailRatio), MathF.Max(1f / gain, 1f));
            }
        }
        float x0 = MathF.Pow(yb * gain, 1f / PerceptualGamma);
        float x = Shadows(Shadows(x0, p.ShadowsAmount), p.ShadowsAmount2);
        if (x0 > 0f)
            gain *= MathF.Pow(MathF.Max(x, 0f) / x0, PerceptualGamma);
        return Math.Clamp(gain, 1f / MaxLocalGain, MaxLocalGain);
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
    /// The highlights curve on perceptual brightness (1 = white), applied to the base (area) brightness.
    /// <para>
    /// Pulled down: as Lightroom (measured on its TIFFs of the R8 sample, Highlights 0 → −95,
    /// <c>Bench/SliderFitBench</c>), a darkening by a number of stops that grows with the brightness
    /// (<see cref="HighlightsDarkening"/>: none in the deepest shadows, 0.44 stops in the lower midtones, 1.09 at white,
    /// more above white so RAW highlights come back): mostly an exposure reduction weighted towards the bright tones.
    /// A gain keeps the local contrast and the colour ratios; the old curve, which flattened only the upper range, took
    /// 20 % of the detail of bright foliage and turned skies grey. Beyond −100 the second pass applies it again.
    /// </para>
    /// <para>
    /// Pushed up: brightens the upper tones (from <see cref="HighlightsPivot"/>) but keeps white at white (Whites moves
    /// the white point): t + t·(h − t) with h = 1 − (1 − t)^γ, rising everywhere, slope 1 at the pivot and 0 at white.
    /// Values above white are left alone.
    /// </para>
    /// Mirrored in the shader.
    /// </summary>
    public static float Highlights(float x, float amount)
    {
        if (amount < 0f)
            return x * MathF.Pow(2f, amount / PreparedAdjustments.MaxHighlightsShift * HighlightsDarkening(x) / PerceptualGamma);
        if (amount == 0f || x <= HighlightsPivot || x >= 1f)
            return x;
        float t = (x - HighlightsPivot) / (1f - HighlightsPivot);
        float h = 1f - MathF.Pow(1f - t, 1f + HighlightsLiftGamma * amount);
        return HighlightsPivot + (t + t * (h - t)) * (1f - HighlightsPivot);
    }

    /// <summary>
    /// Extra darkening (stops at −100) from perceptual 0.3 to white (0.44 → 1.09 stops). Fitted with
    /// <c>Bench/SliderFitBench.HighlightsGrid</c> to Lightroom's −95 on the R8 sample: tone within 2.5 levels RMS, detail kept
    /// (0.89 to 0.94 per scale, a plain gain gave 0.81; Lightroom 0.91 to 1.05).
    /// </summary>
    public const float HighlightsTopStops = 0.65f;

    /// <summary>
    /// How much of the detail a darkened area loses is put back (1 = the pixel keeps its perceptual distance from its
    /// local mean). Above 1 as Lightroom, whose Highlights −95 raised the finest detail of bright foliage by 5 %.
    /// </summary>
    public const float HighlightsDetailKeep = 1.2f;

    /// <summary>
    /// Stops of darkening at Highlights −100 for a perceptual brightness <paramref name="x"/> (1 = white): fades in over
    /// the deepest shadows (0.02 to 0.12), 0.44 stops up to 0.3, then rising linearly by <see cref="HighlightsTopStops"/>
    /// to white and beyond.
    /// </summary>
    public static float HighlightsDarkening(float x)
    {
        float s = Math.Clamp((x - 0.02f) / 0.1f, 0f, 1f);
        // above white the growth is 0.4 x as steep, so even two passes (-200) never reverse the order of tones
        float rise = MathF.Max(MathF.Min(x, 1f) - 0.3f, 0f) + 0.4f * MathF.Max(x - 1f, 0f);
        return s * s * (3f - 2f * s) * (0.44f + HighlightsTopStops / 0.7f * rise);
    }

    /// <summary>
    /// Strength of <see cref="ShoulderLift"/>: how much of the brightness a RAW pixel had above its local mean, before
    /// the base curve's shoulder pressed it together, comes back (per unit of shoulder expansion beyond
    /// <see cref="ShoulderLiftStart"/>).
    /// </summary>
    public const float ShoulderLiftAmount = 1.5f;

    /// <summary>Shoulder expansion (slope after / before the darkening) below which <see cref="ShoulderLift"/> does nothing.</summary>
    public const float ShoulderLiftStart = 2.5f;

    /// <summary>Lift above the local mean (natural log) treated as grain and left out of <see cref="ShoulderLift"/> (4 %).</summary>
    public const float ShoulderLiftGrain = 0.04f;

    /// <summary>
    /// Extra factor for a RAW pixel brighter than its local mean when Highlights darkens that mean (<see cref="LocalGain"/>).
    /// <para>
    /// The base curve's shoulder (<see cref="RawBaseCurve"/>) presses everything near white together: on the R8 sample a
    /// cloud edge (scene 1.7, twice the sky) and the blue sky next to it render 0.985 and 0.93 linear, and the cloud's
    /// local mean is even darker than the sky's. A darkening of the mean alone brought both to the same grey (luma 191 and
    /// 183), where Lightroom, which darkens before its curve, keeps the cloud a stop brighter (214 and 185). Darkened,
    /// the mean leaves the shoulder: its log-slope grows by e = <see cref="ShoulderSlope"/> after / before (about 5 for
    /// clouds, 1.5 for foliage highlights). The pixel's lift above the mean, in natural log, is multiplied by
    /// 1 + <see cref="ShoulderLiftAmount"/> · (e − <see cref="ShoulderLiftStart"/>) beyond <see cref="ShoulderLiftGrain"/>:
    /// only where the shoulder pressed hard, only upwards (darker pixels pushed down left dark blotches in the sky) and not
    /// the sky's grain, and at most back to the pixel's own brightness. Lightroom −95 on the R8 sample: cloud edge 215 (Lightroom 214, before 191), sky unchanged, tone
    /// 2.1 levels RMS (before 2.5).
    /// </para>
    /// Mirrored in the shader.
    /// </summary>
    public static float ShoulderLift(float meanLuminance, float meanGain, float detailRatio)
    {
        float e = ShoulderSlope(meanLuminance * meanGain) / ShoulderSlope(meanLuminance);
        float lift = MathF.Max(-MathF.Log(MathF.Max(detailRatio, 1e-6f)) - ShoulderLiftGrain, 0f);
        return MathF.Exp(ShoulderLiftAmount * MathF.Max(e - ShoulderLiftStart, 0f) * lift);
    }

    /// <summary>
    /// The base curve's log-slope (d ln display / d ln scene, over <see cref="RawBaseCurve.Contrast"/>) at the display
    /// value <paramref name="y"/>: 1 in the midtones, towards 0 on the shoulder.
    /// </summary>
    public static float ShoulderSlope(float y)
    {
        y = MathF.Max(y, 1e-6f);
        float w2 = RawBaseCurve.WhiteZ * RawBaseCurve.WhiteZ;
        float z = 2 * y / (1 - y + MathF.Sqrt((1 - y) * (1 - y) + 4 * y / w2));
        return 1f + z / (w2 + z) - z / (1f + z);
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
