using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.ViewModels;

/// <summary>One component of the selected mask: how it combines, invert, delete.</summary>
public partial class ComponentItemViewModel : ViewModelBase
{
    private readonly Action<int, Func<MaskComponent, MaskComponent>, string?> _edit;
    private readonly Action<int> _delete;
    private bool _updating;

    public ComponentItemViewModel(int index, MaskComponent component,
        Action<int, Func<MaskComponent, MaskComponent>, string?> edit, Action<int> delete)
    {
        Index = index;
        _edit = edit;
        _delete = delete;
        Title = component.DisplayName;
        _updating = true;
        Mode = component.Mode;
        Invert = component.Invert;
        HasFeather = component is RadialGradientComponent;
        Feather = component is RadialGradientComponent r ? r.Feather * 100 : 0;
        _updating = false;
    }

    public int Index { get; }
    public string Title { get; }

    public IReadOnlyList<MaskMode> Modes { get; } = Enum.GetValues<MaskMode>();

    /// <summary>The first component always starts the mask, so its mode is not editable.</summary>
    public bool CanChangeMode => Index > 0;

    [ObservableProperty]
    public partial MaskMode Mode { get; set; }

    [ObservableProperty]
    public partial bool Invert { get; set; }

    /// <summary>Radial gradients have an editable feather (0..100).</summary>
    public bool HasFeather { get; }

    [ObservableProperty]
    public partial double Feather { get; set; }

    partial void OnFeatherChanged(double value)
    {
        if (!_updating)
            _edit(Index, c => c is RadialGradientComponent r ? r with { Feather = (float)Math.Round(value) / 100f } : c, $"feather:{Index}");
    }

    partial void OnModeChanged(MaskMode value)
    {
        if (!_updating)
            _edit(Index, c => c with { Mode = value }, null);
    }

    partial void OnInvertChanged(bool value)
    {
        if (!_updating)
            _edit(Index, c => c with { Invert = value }, null);
    }

    [RelayCommand]
    private void Delete() => _delete(Index);

    /// <summary>True if this row already displays <paramref name="component"/> correctly.</summary>
    public bool Shows(MaskComponent component) =>
        component.DisplayName == Title && component.Mode == Mode && component.Invert == Invert
        && (component is not RadialGradientComponent r || Math.Abs(r.Feather * 100 - Feather) < 0.5);
}
