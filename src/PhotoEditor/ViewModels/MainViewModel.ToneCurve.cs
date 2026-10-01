using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Controls;
using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.ViewModels;

/// <summary>Tone curve panel: the point curve editor (the parametric sliders are ordinary parameters).</summary>
public partial class MainViewModel
{
    public IReadOnlyList<string> CurveChannels { get; } = ["RGB", "Red", "Green", "Blue"];

    /// <summary>Which point curve the editor shows: 0 = RGB, 1 = red, 2 = green, 3 = blue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCurve), nameof(CurveBase), nameof(CurveColor))]
    public partial int CurveChannelIndex { get; set; }

    /// <summary>The point curve of the chosen channel.</summary>
    public PointCurve SelectedCurve => CurveOf(State.Adjustments, CurveChannelIndex);

    /// <summary>The parametric curve, drawn behind the RGB point curve (null when it changes nothing).</summary>
    public double[]? CurveBase
    {
        get
        {
            var a = State.Adjustments;
            if (CurveChannelIndex != 0 || (a.CurveHighlights == 0 && a.CurveLights == 0 && a.CurveDarks == 0 && a.CurveShadows == 0))
                return null;
            return ToneCurveTable.Parametric(a);
        }
    }

    public Color CurveColor => CurveChannelIndex switch
    {
        1 => Color.FromRgb(0xf0, 0x60, 0x60),
        2 => Color.FromRgb(0x60, 0xd0, 0x60),
        3 => Color.FromRgb(0x70, 0x90, 0xff),
        _ => Color.FromRgb(0xe8, 0xe8, 0xe8),
    };

    private static PointCurve CurveOf(AdjustmentSettings a, int channel) => channel switch
    {
        1 => a.CurveRed,
        2 => a.CurveGreen,
        3 => a.CurveBlue,
        _ => a.Curve,
    };

    private static AdjustmentSettings WithCurve(AdjustmentSettings a, int channel, PointCurve curve) => channel switch
    {
        1 => a with { CurveRed = curve },
        2 => a with { CurveGreen = curve },
        3 => a with { CurveBlue = curve },
        _ => a with { Curve = curve },
    };

    private int _curveDrag;

    /// <summary>An edit from the curve editor; one drag is one undo step.</summary>
    [RelayCommand]
    private void EditCurve(CurveEdit edit)
    {
        if (edit.Started)
            _curveDrag++;
        ApplyEdit(State with { Adjustments = WithCurve(State.Adjustments, CurveChannelIndex, edit.Curve.Normalized()) },
            $"curve.{CurveChannelIndex}.{_curveDrag}");
    }

    /// <summary>Straightens the chosen channel's point curve.</summary>
    [RelayCommand]
    private void ResetCurve() => ApplyEdit(State with { Adjustments = WithCurve(State.Adjustments, CurveChannelIndex, PointCurve.Linear) });

    /// <summary>Called when the edit changed: refreshes the editor if the tone curve did.</summary>
    private void RefreshToneCurve(AdjustmentSettings before, AdjustmentSettings after)
    {
        if (before.Curve != after.Curve || before.CurveRed != after.CurveRed || before.CurveGreen != after.CurveGreen
            || before.CurveBlue != after.CurveBlue)
            OnPropertyChanged(nameof(SelectedCurve));
        if (before.CurveHighlights != after.CurveHighlights || before.CurveLights != after.CurveLights || before.CurveDarks != after.CurveDarks
            || before.CurveShadows != after.CurveShadows || before.CurveShadowSplit != after.CurveShadowSplit
            || before.CurveMidtoneSplit != after.CurveMidtoneSplit || before.CurveHighlightSplit != after.CurveHighlightSplit)
            OnPropertyChanged(nameof(CurveBase));
    }
}
