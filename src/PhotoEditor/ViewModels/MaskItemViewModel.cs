using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.ViewModels;

/// <summary>A row in the mask list. Changes are sent back to the owner as edits.</summary>
public partial class MaskItemViewModel : ViewModelBase
{
    private readonly Action<Guid, Func<Mask, Mask>, string> _edit;
    private bool _updating;

    public MaskItemViewModel(Mask mask, Action<Guid, Func<Mask, Mask>, string> edit)
    {
        Id = mask.Id;
        _edit = edit;
        Update(mask);
    }

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    partial void OnNameChanged(string value)
    {
        if (!_updating)
            _edit(Id, m => m with { Name = value }, $"name:{Id}");
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_updating)
            _edit(Id, m => m with { Enabled = value }, $"enabled:{Id}");
    }

    /// <summary>Refreshes from the model without producing an edit.</summary>
    public void Update(Mask mask)
    {
        _updating = true;
        Name = mask.Name;
        IsEnabled = mask.Enabled;
        _updating = false;
    }
}
