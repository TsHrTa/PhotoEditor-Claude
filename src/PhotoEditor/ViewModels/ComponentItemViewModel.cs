using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.ViewModels;

/// <summary>One component of the selected mask: how it combines, invert, delete.</summary>
public partial class ComponentItemViewModel : ViewModelBase
{
    private readonly Action<int, Func<MaskComponent, MaskComponent>> _edit;
    private readonly Action<int> _delete;
    private bool _updating;

    public ComponentItemViewModel(int index, MaskComponent component,
        Action<int, Func<MaskComponent, MaskComponent>> edit, Action<int> delete)
    {
        Index = index;
        _edit = edit;
        _delete = delete;
        Title = component.DisplayName;
        _updating = true;
        Mode = component.Mode;
        Invert = component.Invert;
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

    partial void OnModeChanged(MaskMode value)
    {
        if (!_updating)
            _edit(Index, c => c with { Mode = value });
    }

    partial void OnInvertChanged(bool value)
    {
        if (!_updating)
            _edit(Index, c => c with { Invert = value });
    }

    [RelayCommand]
    private void Delete() => _delete(Index);
}
