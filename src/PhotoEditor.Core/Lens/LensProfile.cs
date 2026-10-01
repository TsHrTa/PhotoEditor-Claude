using DModel = PhotoEditor.Core.Lens.DistortionModel;
using TModel = PhotoEditor.Core.Lens.TcaModel;

namespace PhotoEditor.Core.Lens;

/// <summary>
/// A lens's corrections for one shot: calibrations interpolated to the focal length (and aperture for
/// vignetting), with the radius units converted from lensfun's to the photo's (see <see cref="LensCorrection"/>).
/// </summary>
public sealed record LensProfile(
    LensfunLens Lens,
    DistortionModel? DistortionModel,
    double[] Distortion,
    TcaModel? TcaModel,
    double[] TcaRed,
    double[] TcaBlue,
    double[]? Vignetting,
    double HuginUnitMm,
    double VignettingUnitMm)
{
    /// <summary>Diagonal of a full-frame (crop factor 1) sensor in mm.</summary>
    public const double FullFrameDiagonal = 43.2666;

    /// <summary>
    /// Interpolates <paramref name="lens"/>'s calibrations for a shot at <paramref name="focal"/> mm and
    /// <paramref name="aperture"/> (null = unknown: the widest calibrated aperture).
    /// </summary>
    public static LensProfile For(LensfunLens lens, double focal, double? aperture)
    {
        // lensfun: distortion / TCA use r = 1 at half the short side of the calibration sensor, vignetting r = 1
        // at its corner; both in mm here, so any photo can convert its pixel distances.
        double halfDiagonal = FullFrameDiagonal / lens.CropFactor / 2;
        double huginUnit = halfDiagonal / Math.Sqrt(1 + lens.AspectRatio * lens.AspectRatio);

        DistortionModel? distortionModel = null;
        double[] distortion = [];
        if (lens.Distortion.Count > 0)
        {
            // Interpolate among the calibrations of the model used nearest to this focal length.
            var nearest = lens.Distortion.MinBy(d => Math.Abs(d.Focal - focal))!;
            var same = lens.Distortion.Where(d => d.Model == nearest.Model).OrderBy(d => d.Focal).ToList();
            distortionModel = nearest.Model;
            distortion = Interpolate(same, d => d.Focal, d => d.Terms, focal);
        }

        TcaModel? tcaModel = null;
        double[] red = [], blue = [];
        if (lens.Tca.Count > 0)
        {
            var nearest = lens.Tca.MinBy(t => Math.Abs(t.Focal - focal))!;
            var same = lens.Tca.Where(t => t.Model == nearest.Model).OrderBy(t => t.Focal).ToList();
            tcaModel = nearest.Model;
            red = Interpolate(same, t => t.Focal, t => t.Red, focal);
            blue = Interpolate(same, t => t.Focal, t => t.Blue, focal);
        }

        return new LensProfile(lens, distortionModel, distortion, tcaModel, red, blue,
            InterpolateVignetting(lens.Vignetting, focal, aperture), huginUnit, halfDiagonal);
    }

    /// <summary>Linear interpolation of the terms between the two calibrations around <paramref name="x"/> (clamped at the ends).</summary>
    private static double[] Interpolate<T>(IReadOnlyList<T> sorted, Func<T, double> key, Func<T, double[]> terms, double x)
    {
        if (x <= key(sorted[0]))
            return terms(sorted[0]);
        if (x >= key(sorted[^1]))
            return terms(sorted[^1]);
        for (int i = 1; i < sorted.Count; i++)
        {
            double x0 = key(sorted[i - 1]), x1 = key(sorted[i]);
            if (x > x1)
                continue;
            double t = x1 > x0 ? (x - x0) / (x1 - x0) : 0;
            var a = terms(sorted[i - 1]);
            var b = terms(sorted[i]);
            return a.Select((v, j) => v + (b[j] - v) * t).ToArray();
        }
        return terms(sorted[^1]);
    }

    /// <summary>
    /// k1..k3 for the shot: for each calibrated focal length, the calibrations at the farthest distance, interpolated
    /// in aperture (in stops); then interpolated in focal length. Null without data.
    /// </summary>
    private static double[]? InterpolateVignetting(IReadOnlyList<VignettingCalibration> all, double focal, double? aperture)
    {
        if (all.Count == 0)
            return null;
        var perFocal = new List<(double Focal, double[] K)>();
        foreach (var group in all.GroupBy(v => v.Focal).OrderBy(g => g.Key))
        {
            double far = group.Max(v => v.Distance);
            var byAperture = group.Where(v => v.Distance == far && v.Aperture > 0)
                .OrderBy(v => v.Aperture).ToList();
            if (byAperture.Count == 0)
                continue;
            double stops = Math.Log2(aperture is > 0 ? aperture.Value : byAperture[0].Aperture);
            perFocal.Add((group.Key, Interpolate(byAperture, v => Math.Log2(v.Aperture), v => [v.K1, v.K2, v.K3], stops)));
        }
        return perFocal.Count == 0 ? null : Interpolate(perFocal, p => p.Focal, p => p.K, focal);
    }

    /// <summary>Distorted radius for an undistorted one (lensfun Hugin units).</summary>
    public double Distort(double ru) => DistortionModel switch
    {
        DModel.Poly3 => ru * (1 - Distortion[0] + Distortion[0] * ru * ru),
        DModel.Poly5 => ru * (1 + Distortion[0] * ru * ru + Distortion[1] * ru * ru * ru * ru),
        DModel.PtLens => ru * (Distortion[0] * ru * ru * ru + Distortion[1] * ru * ru + Distortion[2] * ru
            + 1 - Distortion[0] - Distortion[1] - Distortion[2]),
        _ => ru,
    };

    /// <summary>Radius of the red / blue image for the green one's radius <paramref name="r"/> (Hugin units).</summary>
    public (double Red, double Blue) Tca(double r) => TcaModel switch
    {
        TModel.Linear => (r * TcaRed[0], r * TcaBlue[0]),
        TModel.Poly3 => (r * (TcaRed[0] + TcaRed[1] * r + TcaRed[2] * r * r), r * (TcaBlue[0] + TcaBlue[1] * r + TcaBlue[2] * r * r)),
        _ => (r, r),
    };
}
