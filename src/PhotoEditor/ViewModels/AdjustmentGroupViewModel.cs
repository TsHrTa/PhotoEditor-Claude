using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoEditor.ViewModels;

/// <summary>A titled panel section of sliders (e.g. "Light").</summary>
public sealed partial class AdjustmentGroupViewModel(string title, IReadOnlyList<ParameterViewModel> parameters, bool isExpanded)
    : ViewModelBase
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = isExpanded;

    public string Title { get; } = title;
    public IReadOnlyList<ParameterViewModel> Parameters { get; } = parameters;
}
