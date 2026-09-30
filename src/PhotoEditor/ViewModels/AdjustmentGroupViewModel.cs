using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoEditor.ViewModels;

/// <summary>A titled panel section of sliders (e.g. "Light").</summary>
public sealed partial class AdjustmentGroupViewModel(string title, IReadOnlyList<ParameterViewModel> parameters, bool isExpanded)
    : ViewModelBase
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = isExpanded;

    /// <summary>False for whole-image-only groups (Detail) while a mask is selected.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>The group only applies to the whole image, not inside masks.</summary>
    public bool IsGlobalOnly { get; init; }

    /// <summary>For the lens corrections group: the view model with the profile switches (null for other groups).</summary>
    public MainViewModel? Lens { get; init; }

    public string Title { get; } = title;
    public IReadOnlyList<ParameterViewModel> Parameters { get; } = parameters;
}
