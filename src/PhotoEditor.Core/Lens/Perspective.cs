using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.Core.Lens;

/// <summary>A 3 × 3 projective transform of 2D points (row-major, column vectors).</summary>
public readonly record struct Matrix3(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static readonly Matrix3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Matrix3 Scale(double sx, double sy) => new(sx, 0, 0, 0, sy, 0, 0, 0, 1);

    public static Matrix3 Translate(double tx, double ty) => new(1, 0, tx, 0, 1, ty, 0, 0, 1);

    /// <summary>Rotation by <paramref name="degrees"/>, clockwise on screen (y down).</summary>
    public static Matrix3 Rotate(double degrees)
    {
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary>This after <paramref name="b"/>: (this × b)(p) = this(b(p)).</summary>
    public static Matrix3 operator *(Matrix3 a, Matrix3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public (double X, double Y) Apply(double x, double y)
    {
        double w = M20 * x + M21 * y + M22;
        return ((M00 * x + M01 * y + M02) / w, (M10 * x + M11 * y + M12) / w);
    }

    public Matrix3 Inverse()
    {
        double c00 = M11 * M22 - M12 * M21, c01 = M12 * M20 - M10 * M22, c02 = M10 * M21 - M11 * M20;
        double det = M00 * c00 + M01 * c01 + M02 * c02;
        if (Math.Abs(det) < 1e-300)
            throw new InvalidOperationException("The transform cannot be inverted.");
        double k = 1 / det;
        return new(
            c00 * k, (M02 * M21 - M01 * M22) * k, (M01 * M12 - M02 * M11) * k,
            c01 * k, (M00 * M22 - M02 * M20) * k, (M02 * M10 - M00 * M12) * k,
            c02 * k, (M01 * M20 - M00 * M21) * k, (M00 * M11 - M01 * M10) * k);
    }

    /// <summary>The 9 values row by row, as floats (for the shader).</summary>
    public float[] ToArray() => [(float)M00, (float)M01, (float)M02, (float)M10, (float)M11, (float)M12, (float)M20, (float)M21, (float)M22];
}

/// <summary>
/// The Transform panel (Lightroom's manual Transform): keystone (vertical / horizontal perspective), rotation, aspect,
/// scale and offset, as a projective map from the output to the photo. Positions are in pixels from the photo's
/// centre. The keystone turns the picture plane in 3D in front of a lens with a normal field of view (focal length =
/// the photo's diagonal) and projects it back.
/// </summary>
public sealed record Perspective(Matrix3 Fill, Matrix3 Full)
{
    /// <summary>Tilt of the picture plane at Vertical / Horizontal ±100.</summary>
    public const double MaxTiltDegrees = 45;

    /// <summary>Stretch at Aspect ±100 (× 1.5 wider, or taller).</summary>
    public const double MaxAspectStretch = 0.5;

    /// <summary>Shift at X / Y Offset ±100, as a fraction of the photo's width / height.</summary>
    public const double MaxOffset = 0.25;

    public static bool IsIdentity(AdjustmentSettings s) =>
        s.TransformVertical == 0 && s.TransformHorizontal == 0 && s.TransformRotate == 0 && s.TransformAspect == 0
        && s.TransformScale == 100 && s.TransformOffsetX == 0 && s.TransformOffsetY == 0;

    /// <summary>
    /// The transform of a <paramref name="width"/> × <paramref name="height"/> photo, or null when the settings change
    /// nothing. <see cref="Fill"/> leaves out the scale and offset (it decides how far the result is enlarged to fill
    /// the frame); <see cref="Full"/> is the whole map. Both map an output position to the photo position.
    /// </summary>
    public static Perspective? For(AdjustmentSettings s, int width, int height)
    {
        if (IsIdentity(s))
            return null;
        double f = Math.Sqrt((double)width * width + (double)height * height);
        // Keystone, photo → output: turn the plane (x, y, f) and project it back at f; the centre stays.
        // Vertical < 0 widens the top (straightens a building that leans back); Horizontal > 0 enlarges the right side.
        double tv = -Math.Clamp(s.TransformVertical, -100, 100) / 100 * MaxTiltDegrees * Math.PI / 180;
        double th = Math.Clamp(s.TransformHorizontal, -100, 100) / 100 * MaxTiltDegrees * Math.PI / 180;
        var rx = new Matrix3(1, 0, 0, 0, Math.Cos(tv), -Math.Sin(tv), 0, Math.Sin(tv), Math.Cos(tv));
        var ry = new Matrix3(Math.Cos(th), 0, Math.Sin(th), 0, 1, 0, -Math.Sin(th), 0, Math.Cos(th));
        var k = Matrix3.Scale(f, f);
        var kInv = Matrix3.Scale(1 / f, 1 / f);
        var turn = k * ry * rx * kInv;
        var (x0, y0) = turn.Apply(0, 0);
        var keystone = Matrix3.Translate(-x0, -y0) * turn;

        double a = Math.Clamp(s.TransformAspect, -100, 100) / 100 * MaxAspectStretch;
        var aspect = Matrix3.Scale(a > 0 ? 1 + a : 1, a < 0 ? 1 - a : 1);
        var rotate = Matrix3.Rotate(Math.Clamp(s.TransformRotate, -10, 10));
        double scale = Math.Clamp(s.TransformScale, 50, 150) / 100;
        var place = Matrix3.Translate(Math.Clamp(s.TransformOffsetX, -100, 100) / 100 * MaxOffset * width,
            Math.Clamp(s.TransformOffsetY, -100, 100) / 100 * MaxOffset * height) * Matrix3.Scale(scale, scale);

        // Photo → output, then inverted (the warp asks where each output pixel comes from).
        var fill = aspect * rotate * keystone;
        return new Perspective(fill.Inverse(), (place * fill).Inverse());
    }
}
