using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoEditor.Core.Adjustments;

namespace PhotoEditor.ViewModels;

/// <summary>One adjustment slider bound to a parameter of the current <see cref="AdjustmentSettings"/>.</summary>
public partial class ParameterViewModel : ViewModelBase
{
    private readonly Func<AdjustmentSettings> _getSettings;
    private readonly Action<AdjustmentSettings> _setSettings;
    private readonly Func<AdjustmentSettings>? _getDefaults;

    public ParameterViewModel(AdjustmentParameter parameter, Func<AdjustmentSettings> getSettings, Action<AdjustmentSettings> setSettings,
        Func<AdjustmentSettings>? getDefaults = null)
    {
        Parameter = parameter;
        _getSettings = getSettings;
        _setSettings = setSettings;
        _getDefaults = getDefaults;
    }

    public AdjustmentParameter Parameter { get; }
    public string Label => Parameter.Label;
    public double Minimum => Parameter.Minimum;
    public double Maximum => Parameter.Maximum;

    public double Value
    {
        get => Parameter.Get(_getSettings());
        set
        {
            if (value.Equals(Value))
                return;
            _setSettings(Parameter.Set(_getSettings(), value));
        }
    }

    public string DisplayValue => Value.ToString(Parameter.Format);

    /// <summary>Back to where the photo started (e.g. sharpening 40 on a RAW).</summary>
    public void Reset() => Value = _getDefaults is { } defaults ? Parameter.Get(defaults()) : Parameter.DefaultValue;

    /// <summary>Called when the settings changed from anywhere (slider, reset, undo…).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
    }
}
