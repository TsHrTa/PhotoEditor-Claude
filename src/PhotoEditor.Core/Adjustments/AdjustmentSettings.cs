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
