using System.Collections.Generic;

namespace PhotoEditor.ViewModels;

/// <summary>A titled panel section of sliders (e.g. "Light").</summary>
public sealed class AdjustmentGroupViewModel(string title, IReadOnlyList<ParameterViewModel> parameters)
{
    public string Title { get; } = title;
    public IReadOnlyList<ParameterViewModel> Parameters { get; } = parameters;
}
