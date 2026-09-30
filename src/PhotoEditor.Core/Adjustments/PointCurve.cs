using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace PhotoEditor.Core.Adjustments;

/// <summary>A point of a <see cref="PointCurve"/>: input and output, 0..1 (0 = black, 1 = white).</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// A tone curve through control points (Lightroom's point curve), interpolated with the cubic spline Adobe's DNG
/// SDK uses (<c>dng_spline_solver</c>), so a curve read from a Lightroom XMP has the same shape here. Immutable;
/// compared by its points.
/// </summary>
public sealed record PointCurve
{
    /// <summary>The straight line: no change.</summary>
    public static readonly PointCurve Linear = new() { Points = [new(0, 0), new(1, 1)] };

    /// <summary>Control points, sorted by X, at least two.</summary>
    public ImmutableList<CurvePoint> Points { get; init; } = [new(0, 0), new(1, 1)];

    /// <summary>True when the curve changes nothing (every point on the diagonal).</summary>
    [JsonIgnore]
    public bool IsLinear => Points.All(p => Math.Abs(p.X - p.Y) < 1e-9);

    /// <summary>
    /// The curve with its points inside 0..1, sorted, without two points at (almost) the same X (the later one
    /// wins); fewer than two points give <see cref="Linear"/>.
    /// </summary>
    public PointCurve Normalized()
    {
        var list = new List<CurvePoint>();
        foreach (var p in (Points ?? []).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
                     .Select(p => new CurvePoint(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)))
                     .Select((p, i) => (p, i)).OrderBy(t => t.p.X).ThenBy(t => t.i).Select(t => t.p))
        {
            if (list.Count > 0 && p.X - list[^1].X < MinGap)
                list[^1] = p;
            else
                list.Add(p);
        }
        if (list.Count < 2)
            return Linear;
        var normalized = list.ToImmutableList();
        return normalized.SequenceEqual(Points ?? []) ? this : new PointCurve { Points = normalized };
    }

    /// <summary>Smallest X distance between two points (about half an 8-bit step).</summary>
    public const double MinGap = 0.002;

    public bool Equals(PointCurve? other) =>
        other is not null && (ReferenceEquals(this, other) || Points.SequenceEqual(other.Points));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in Points)
            hash.Add(p);
        return hash.ToHashCode();
    }

    /// <summary>The curve's value at <paramref name="x"/> (0..1): the endpoints' Y outside their range, clamped to 0..1.</summary>
    public double Evaluate(double x) => Spline().Evaluate(x);

    // Keyed by the point list, not stored in a field: a record's "with" copies fields.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImmutableList<CurvePoint>, Solver> Solvers = new();

    /// <summary>The solved spline (made once per point list).</summary>
    internal Solver Spline() => Solvers.GetValue(Points, points => new Solver(points));

    /// <summary>Adobe's spline: slopes from a tridiagonal system, Hermite segments between the points.</summary>
    internal sealed class Solver
    {
        private readonly double[] _x, _y, _s;

        public Solver(IReadOnlyList<CurvePoint> points)
        {
            int n = points.Count;
            _x = points.Select(p => p.X).ToArray();
            _y = points.Select(p => p.Y).ToArray();
            _s = new double[n];
            double a = _x[1] - _x[0];
            double b = (_y[1] - _y[0]) / a;
            _s[0] = b;
            // Slopes as the weighted average of the slopes to the neighbouring points
            for (int j = 2; j < n; j++)
            {
                double c = _x[j] - _x[j - 1];
                double d = (_y[j] - _y[j - 1]) / c;
                _s[j - 1] = (b * c + d * a) / (a + c);
                a = c;
                b = d;
            }
            _s[n - 1] = 2 * b - _s[n - 2];
            _s[0] = 2 * _s[0] - _s[1];
            if (n <= 2)
                return;
            // ... then smoothed by solving for continuous second derivatives
            var e = new double[n];
            var f = new double[n];
            var g = new double[n];
            f[0] = 0.5;
            e[n - 1] = 0.5;
            g[0] = 0.75 * (_s[0] + _s[1]);
            g[n - 1] = 0.75 * (_s[n - 2] + _s[n - 1]);
            for (int j = 1; j < n - 1; j++)
            {
                double span = (_x[j + 1] - _x[j - 1]) * 2;
                e[j] = (_x[j + 1] - _x[j]) / span;
                f[j] = (_x[j] - _x[j - 1]) / span;
                g[j] = 1.5 * _s[j];
            }
            for (int j = 1; j < n; j++)
            {
                double pivot = 1 - f[j - 1] * e[j];
                if (j != n - 1)
                    f[j] /= pivot;
                g[j] = (g[j] - g[j - 1] * e[j]) / pivot;
            }
            for (int j = n - 2; j >= 0; j--)
                g[j] -= f[j] * g[j + 1];
            Array.Copy(g, _s, n);
        }

        public double Evaluate(double x)
        {
            int n = _x.Length;
            if (x <= _x[0])
                return Math.Clamp(_y[0], 0, 1);
            if (x >= _x[n - 1])
                return Math.Clamp(_y[n - 1], 0, 1);
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_x[mid] <= x)
                    lo = mid;
                else
                    hi = mid;
            }
            double x0 = _x[lo], x1 = _x[hi], y0 = _y[lo], y1 = _y[hi], s0 = _s[lo], s1 = _s[hi];
            double span = x1 - x0;
            double t = (x - x0) / span, u = (x1 - x) / span;
            double v = (y0 * (2 - u + t) + s0 * span * t) * (u * u) + (y1 * (2 - t + u) - s1 * span * u) * (t * t);
            return Math.Clamp(v, 0, 1);
        }
    }
}
