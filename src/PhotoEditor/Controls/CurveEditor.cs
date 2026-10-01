using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.Controls;

/// <summary>A change made in <see cref="CurveEditor"/>: the new curve; <see cref="Started"/> on the first change of a drag.</summary>
public sealed record CurveEdit(PointCurve Curve, bool Started);

/// <summary>
/// Point curve editor (Lightroom-style): a click adds a point and drags it, points can be dragged (not past their
/// neighbours), a double-click, right-click or dragging a middle point out of the box removes it. Edits are sent
/// to <see cref="EditCommand"/> as <see cref="CurveEdit"/>; the shown curve is <see cref="Curve"/>.
/// </summary>
public sealed class CurveEditor : Control
{
    public static readonly StyledProperty<PointCurve> CurveProperty =
        AvaloniaProperty.Register<CurveEditor, PointCurve>(nameof(Curve), PointCurve.Linear);

    /// <summary>A curve drawn behind (dashed), sampled evenly over 0..1: the parametric curve. Null = none.</summary>
    public static readonly StyledProperty<double[]?> BaseCurveProperty =
        AvaloniaProperty.Register<CurveEditor, double[]?>(nameof(BaseCurve));

    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<CurveEditor, Color>(nameof(LineColor), Colors.WhiteSmoke);

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<CurveEditor, ICommand?>(nameof(EditCommand));

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(CurveProperty, BaseCurveProperty, LineColorProperty);
        FocusableProperty.OverrideDefaultValue<CurveEditor>(true);
    }

    public PointCurve Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public double[]? BaseCurve
    {
        get => GetValue(BaseCurveProperty);
        set => SetValue(BaseCurveProperty, value);
    }

    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public ICommand? EditCommand
    {
        get => GetValue(EditCommandProperty);
        set => SetValue(EditCommandProperty, value);
    }

    private const double Inset = 7, HitRadius = 9, RemoveDistance = 24;

    /// <summary>The points while a drag is going on (the shown curve is then this, not <see cref="Curve"/>).</summary>
    private List<CurvePoint>? _drag;
    private int _dragIndex = -1;
    private bool _dragRemoved, _dragStarted;
    private int _hover = -1;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        return new Size(w, Math.Min(w, double.IsInfinity(availableSize.Height) ? w : availableSize.Height));
    }

    private Rect Box => new(Inset, Inset, Math.Max(1, Bounds.Width - 2 * Inset), Math.Max(1, Bounds.Height - 2 * Inset));

    private Point ToScreen(double x, double y)
    {
        var b = Box;
        return new Point(b.X + x * b.Width, b.Y + (1 - y) * b.Height);
    }

    private (double X, double Y) FromScreen(Point p)
    {
        var b = Box;
        return ((p.X - b.X) / b.Width, 1 - (p.Y - b.Y) / b.Height);
    }

    private IReadOnlyList<CurvePoint> ShownPoints =>
        _drag is null ? Curve.Points : _dragRemoved ? _drag.Where((_, i) => i != _dragIndex).ToList() : _drag;

    public override void Render(DrawingContext context)
    {
        var b = Box;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), new Rect(Bounds.Size));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double t = i / 4.0;
            context.DrawLine(grid, new Point(b.X + t * b.Width, b.Y), new Point(b.X + t * b.Width, b.Bottom));
            context.DrawLine(grid, new Point(b.X, b.Y + t * b.Height), new Point(b.Right, b.Y + t * b.Height));
        }
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xff, 0xff, 0xff)), 1, DashStyle.Dash),
            ToScreen(0, 0), ToScreen(1, 1));

        if (BaseCurve is { Length: > 1 } baseCurve)
        {
            var dashed = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xc0, 0xc0, 0xc0)), 1.2, DashStyle.Dash);
            DrawPolyline(context, dashed, 256, x => baseCurve[(int)Math.Round(x * (baseCurve.Length - 1))]);
        }

        var points = ShownPoints;
        var shown = points.Count >= 2 ? new PointCurve { Points = [.. points] } : PointCurve.Linear;
        var line = new Pen(new SolidColorBrush(LineColor), 1.8);
        DrawPolyline(context, line, 256, shown.Evaluate);

        var fill = new SolidColorBrush(LineColor);
        var outline = new Pen(new SolidColorBrush(LineColor), 1.5);
        for (int i = 0; i < points.Count; i++)
        {
            var c = ToScreen(points[i].X, points[i].Y);
            bool active = (_drag is not null && i == _dragIndex && !_dragRemoved) || (_drag is null && i == _hover);
            context.DrawEllipse(active ? fill : Brushes.Black, outline, c, 4, 4);
        }
    }

    private void DrawPolyline(DrawingContext context, IPen pen, int samples, Func<double, double> f)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToScreen(0, f(0)), false);
            for (int i = 1; i <= samples; i++)
            {
                double x = i / (double)samples;
                g.LineTo(ToScreen(x, f(x)));
            }
            g.EndFigure(false);
        }
        using (context.PushClip(new Rect(Bounds.Size)))
            context.DrawGeometry(null, pen, geometry);
    }

    private int HitPoint(Point p)
    {
        var points = Curve.Points;
        int best = -1;
        double bestDistance = HitRadius;
        for (int i = 0; i < points.Count; i++)
        {
            var c = ToScreen(points[i].X, points[i].Y);
            double d = Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
            if (d <= bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        int hit = HitPoint(p);
        var points = Curve.Points.ToList();
        if (props.IsRightButtonPressed || (props.IsLeftButtonPressed && e.ClickCount == 2))
        {
            // Remove a middle point (the ends stay: a curve needs two).
            if (hit > 0 && hit < points.Count - 1)
            {
                points.RemoveAt(hit);
                Send(points, started: true);
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;
        _drag = points;
        _dragRemoved = false;
        _dragStarted = false;
        if (hit >= 0)
        {
            _dragIndex = hit;
        }
        else
        {
            // A new point where clicked, between the points around it.
            var (x, y) = FromScreen(p);
            x = Math.Clamp(x, 0, 1);
            int index = points.FindIndex(q => q.X > x);
            if (index <= 0)
            {
                _drag = null; // left of the first or right of the last point: move that end instead
                _dragIndex = -1;
                return;
            }
            if (x - points[index - 1].X < PointCurve.MinGap * 2 || points[index].X - x < PointCurve.MinGap * 2)
            {
                _drag = null;
                return;
            }
            points.Insert(index, new CurvePoint(x, Math.Clamp(y, 0, 1)));
            _dragIndex = index;
            Send(points, started: true);
            _dragStarted = true;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag is null)
        {
            int hover = HitPoint(p);
            if (hover != _hover)
            {
                _hover = hover;
                InvalidateVisual();
            }
            return;
        }
        var (x, y) = FromScreen(p);
        int i = _dragIndex;
        bool end = i == 0 || i == _drag.Count - 1;
        var b = Box;
        // Dragging a middle point well out of the box removes it (put back when dragged in again).
        bool outside = p.Y < b.Y - RemoveDistance || p.Y > b.Bottom + RemoveDistance
            || p.X < b.X - RemoveDistance || p.X > b.Right + RemoveDistance;
        bool removed = !end && outside;
        double lo = i > 0 ? _drag[i - 1].X + PointCurve.MinGap * 2 : 0;
        double hi = i < _drag.Count - 1 ? _drag[i + 1].X - PointCurve.MinGap * 2 : 1;
        var moved = new CurvePoint(Math.Clamp(x, lo, Math.Max(lo, hi)), Math.Clamp(y, 0, 1));
        if (moved == _drag[i] && removed == _dragRemoved)
            return;
        _drag[i] = moved;
        _dragRemoved = removed;
        Send(removed ? _drag.Where((_, k) => k != i).ToList() : _drag, started: !_dragStarted);
        _dragStarted = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
            return;
        _drag = null;
        _dragIndex = -1;
        _dragRemoved = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    private void Send(IReadOnlyList<CurvePoint> points, bool started)
    {
        var edit = new CurveEdit(new PointCurve { Points = points.ToImmutableList() }, started);
        if (EditCommand is { } command && command.CanExecute(edit))
            command.Execute(edit);
    }
}
