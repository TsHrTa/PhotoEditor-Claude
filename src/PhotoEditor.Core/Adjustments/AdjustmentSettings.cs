using System.Text.Json.Serialization;

namespace PhotoEditor.Core.Adjustments;

/// <summary>
/// Immutable set of global adjustments. The original image is never changed;
/// these values are applied on every render (preview shader and CPU export).
/// All values are 0 when neutral.
/// </summary>
public sealed record AdjustmentSettings
{
    public static readonly AdjustmentSettings Default = new();

    /// <summary>
    /// Where a RAW photo starts, like Lightroom's defaults for RAWs: sharpening 40 (radius 1, masking 0) and colour
    /// noise reduction 25, because a RAW has had neither in the camera. JPEGs and other finished images start at
    /// <see cref="Default"/>.
    /// </summary>
    public static readonly AdjustmentSettings RawDefault = new() { SharpenAmount = 40, NoiseColor = 25 };

    /// <summary>The settings a photo starts with: <see cref="RawDefault"/> for RAWs, else <see cref="Default"/>.</summary>
    public static AdjustmentSettings DefaultFor(bool isRaw) => isRaw ? RawDefault : Default;

    /// <summary>Exposure in stops (EV), -5..+5.</summary>
    public double Exposure { get; init; }

    /// <summary>-100..+100 for this and the following tone values.</summary>
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }

    /// <summary>-100 (cooler/blue) .. +100 (warmer/yellow).</summary>
    public double Temperature { get; init; }

    /// <summary>-100 (green) .. +100 (magenta).</summary>
    public double Tint { get; init; }

    /// <summary>-100 (greyscale) .. +100.</summary>
    public double Saturation { get; init; }

    /// <summary>-100..+100; like saturation but affects muted colours more than saturated ones.</summary>
    public double Vibrance { get; init; }

    /// <summary>Vignette: -100 (dark edges) .. +100 (light edges).</summary>
    public double VignetteAmount { get; init; }

    /// <summary>0..100: where the vignette transition is centred (lower = reaches further in).</summary>
    public double VignetteMidpoint { get; init; } = 50;

    /// <summary>-100 (rectangular) .. 0 (follows the frame) .. +100 (circular).</summary>
    public double VignetteRoundness { get; init; }

    /// <summary>0..100: width of the transition.</summary>
    public double VignetteFeather { get; init; } = 50;

    /// <summary>Sharpening (unsharp mask on luminance): 0..150.</summary>
    public double SharpenAmount { get; init; }

    /// <summary>Blur radius of the unsharp mask in full-resolution pixels, 0.5..3.</summary>
    public double SharpenRadius { get; init; } = 1;

    /// <summary>0..100: higher values limit sharpening to edges (keeps smooth areas and noise unsharpened).</summary>
    public double SharpenMasking { get; init; }

    /// <summary>
    /// AI noise reduction 0..100: blend between the original and the AI-denoised photo (computed once per photo).
    /// Applied to the source pixels before everything else; whole image only.
    /// </summary>
    public double DenoiseAmount { get; init; }

    /// <summary>
    /// AI deblur / sharpen 0..100: blend towards the AI-deblurred photo (computed once per photo, from the denoised
    /// photo when Denoise is on). Whole image only.
    /// </summary>
    public double DeblurAmount { get; init; }

    /// <summary>
    /// Soften 0..100: removes fine detail and texture (skin, noise) with an edge-preserving blur of the original
    /// photo; strong edges stay. Also works in masks.
    /// </summary>
    public double Soften { get; init; }

    /// <summary>Luminance noise reduction 0..100 (edge-preserving smoothing of brightness only). Whole image only.</summary>
    public double NoiseLuminance { get; init; }

    /// <summary>Colour noise reduction 0..100 (smooths colour blotches, keeps brightness detail). Whole image only.</summary>
    public double NoiseColor { get; init; }

    /// <summary>Removes purple fringes at high-contrast edges, 0..100. Whole image only.</summary>
    public double DefringePurple { get; init; }

    /// <summary>Removes green fringes at high-contrast edges, 0..100. Whole image only.</summary>
    public double DefringeGreen { get; init; }

    /// <summary>Purple defringe hue range in Lightroom's units 0..100 (0 = 240° blue-violet, 100 = 350° magenta-red).</summary>
    public double DefringePurpleHueLow { get; init; } = 30;
    public double DefringePurpleHueHigh { get; init; } = 70;

    /// <summary>Green defringe hue range in Lightroom's units 0..100 (0 = 40° orange-yellow, 100 = 190° cyan).</summary>
    public double DefringeGreenHueLow { get; init; } = 40;
    public double DefringeGreenHueHigh { get; init; } = 60;

    /// <summary>
    /// Lens profile corrections (lensfun): the lens's distortion and vignetting are removed when the camera and lens
    /// are found in the profile database. Whole image only.
    /// </summary>
    public bool LensProfile { get; init; }

    /// <summary>
    /// Removes lateral chromatic aberration (colour edges towards the corners): from the lens profile if it has the
    /// data, otherwise measured in the photo. Whole image only.
    /// </summary>
    public bool RemoveChromaticAberration { get; init; }

    /// <summary>Manual distortion −100..100 (positive straightens barrel distortion, negative pincushion). Whole image only.</summary>
    public double LensDistortion { get; init; }

    /// <summary>Manual lens vignetting −100..100 (positive brightens the corners). Whole image only.</summary>
    public double LensVignetting { get; init; }

    /// <summary>Dehaze −100..100: removes (positive) or adds (negative) haze, based on the photo's haze map. Also in masks.</summary>
    public double Dehaze { get; init; }

    /// <summary>
    /// Texture −100..200: strengthens (positive) or smooths (negative) medium-size detail such as skin, bark or
    /// fabric, without boosting pixel-level noise or strong edges. Also in masks.
    /// </summary>
    public double Texture { get; init; }

    /// <summary>
    /// Clarity −100..200: local contrast of areas against their surroundings (mostly midtones), edge-aware so it
    /// makes no halos; negative gives a soft, flat look. Also in masks.
    /// </summary>
    public double Clarity { get; init; }

    /// <summary>
    /// Tone curve, parametric part (Lightroom's region sliders), −100..100 each: moves the curve in the brightest
    /// quarter (Highlights), the upper middle (Lights), the lower middle (Darks) and the darkest quarter (Shadows).
    /// Whole image only.
    /// </summary>
    public double CurveHighlights { get; init; }
    public double CurveLights { get; init; }
    public double CurveDarks { get; init; }
    public double CurveShadows { get; init; }

    /// <summary>Where the tone curve's regions meet, 0..100 (Lightroom's split points; kept in order when used).</summary>
    public double CurveShadowSplit { get; init; } = 25;
    public double CurveMidtoneSplit { get; init; } = 50;
    public double CurveHighlightSplit { get; init; } = 75;

    /// <summary>Point curve on all three channels (applied after the parametric curve). Whole image only.</summary>
    public PointCurve Curve { get; init; } = PointCurve.Linear;

    /// <summary>Point curves of the single channels (applied after <see cref="Curve"/>). Whole image only.</summary>
    public PointCurve CurveRed { get; init; } = PointCurve.Linear;
    public PointCurve CurveGreen { get; init; } = PointCurve.Linear;
    public PointCurve CurveBlue { get; init; } = PointCurve.Linear;

    // HSL panel, one entry per colour band (see HslBands).
    public HslBand Reds { get; init; } = HslBand.Zero;
    public HslBand Oranges { get; init; } = HslBand.Zero;
    public HslBand Yellows { get; init; } = HslBand.Zero;
    public HslBand Greens { get; init; } = HslBand.Zero;
    public HslBand Aquas { get; init; } = HslBand.Zero;
    public HslBand Blues { get; init; } = HslBand.Zero;
    public HslBand Purples { get; init; } = HslBand.Zero;
    public HslBand Magentas { get; init; } = HslBand.Zero;

    [JsonIgnore]
    public bool IsDefault => this == Default;
}
