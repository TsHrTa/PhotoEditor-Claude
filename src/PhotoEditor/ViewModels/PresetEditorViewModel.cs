using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Masks;
using PhotoEditor.Core.Presets;

namespace PhotoEditor.ViewModels;

/// <summary>A slider choice in the editor ("Light · Exposure").</summary>
public sealed record SliderOption(string Id, string Display)
{
    public override string ToString() => Display;

    public static IReadOnlyList<SliderOption> All { get; } =
        AdjustmentParameters.All.Select(p => new SliderOption(p.Id, $"{p.Group} · {p.Label}")).ToList();

    public static SliderOption Find(string id) =>
        All.FirstOrDefault(o => o.Id == id) ?? new SliderOption(id, id);
}

/// <summary>A choice shown in a combo box for an enum value.</summary>
public sealed record Choice<T>(T Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Edits a <see cref="Preset"/>: its name and an ordered list of steps.</summary>
public sealed partial class PresetEditorViewModel : ViewModelBase
{
    public const string WholeImage = "(whole image)";
    public const string FixedValue = "(fixed value)";
    public const string SameAsRegion = "(the region's mask)";

    private readonly Func<Preset, Task<IReadOnlyList<string>>>? _tryOnPhoto;

    public PresetEditorViewModel(Preset preset, string? originalName, Func<Preset, Task<IReadOnlyList<string>>>? tryOnPhoto)
    {
        OriginalName = originalName;
        _tryOnPhoto = tryOnPhoto;
        Name = preset.Name;
        foreach (var step in preset.Steps)
            Add(StepViewModel.From(step, this));
        RefreshMaskNames();
    }

    /// <summary>Name of the user preset being edited (null for a new one); used to rename.</summary>
    public string? OriginalName { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    /// <summary>Names of masks created by the steps (for the region / reference / fix combo boxes).</summary>
    public ObservableCollection<string> MaskNames { get; } = [];

    public ObservableCollection<string> RegionChoices { get; } = [];
    public ObservableCollection<string> ReferenceChoices { get; } = [];
    public ObservableCollection<string> FixChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTryLog))]
    public partial string TryLog { get; private set; } = "";

    public bool HasTryLog => TryLog.Length > 0;

    public bool CanTry => _tryOnPhoto is not null;

    private void Add(StepViewModel step, int index = -1)
    {
        if (index < 0)
            Steps.Add(step);
        else
            Steps.Insert(index, step);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].Number = i + 1;
    }

    /// <summary>Called when a mask step's name changes.</summary>
    public void RefreshMaskNames()
    {
        var names = Steps.OfType<MaskStepViewModel>().Select(m => m.MaskName.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        Sync(MaskNames, names);
        Sync(RegionChoices, [WholeImage, .. names]);
        Sync(ReferenceChoices, [FixedValue, WholeImage, .. names]);
        Sync(FixChoices, [SameAsRegion, WholeImage, .. names]);
    }

    private static void Sync(ObservableCollection<string> target, List<string> values)
    {
        if (target.SequenceEqual(values))
            return;
        target.Clear();
        foreach (var v in values)
            target.Add(v);
    }

    private string FirstMaskOr(string fallback) => MaskNames.FirstOrDefault() ?? fallback;

    [RelayCommand]
    private void AddAuto() => Add(new AutoStepViewModel(this));

    [RelayCommand]
    private void AddMask()
    {
        string name = "Subject";
        for (int n = 2; MaskNames.Contains(name); n++)
            name = $"Mask {n}";
        Add(new MaskStepViewModel(this) { MaskName = name });
        RefreshMaskNames();
    }

    [RelayCommand]
    private void AddValues() => Add(new ValuesStepViewModel(this) { Target = FirstMaskOr(WholeImage) });

    [RelayCommand]
    private void AddGoal() => Add(new GoalStepViewModel(this) { RegionMask = FirstMaskOr(WholeImage) });

    public void Remove(StepViewModel step)
    {
        Steps.Remove(step);
        Renumber();
        RefreshMaskNames();
    }

    public void Move(StepViewModel step, int delta)
    {
        int i = Steps.IndexOf(step), j = i + delta;
        if (i < 0 || j < 0 || j >= Steps.Count)
            return;
        Steps.Move(i, j);
        Renumber();
    }

    public Preset ToPreset() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Preset" : Name.Trim(),
        Steps = Steps.Select(s => s.ToStep()).ToImmutableList(),
    };

    [RelayCommand]
    private async Task TryOnPhoto()
    {
        if (_tryOnPhoto is null)
            return;
        var log = await _tryOnPhoto(ToPreset());
        TryLog = string.Join(Environment.NewLine, log.Select(l => "• " + l));
    }

    /// <summary>Maps a combo text to a mask name (null for the whole image).</summary>
    public static string? MaskOrNull(string text) =>
        string.IsNullOrWhiteSpace(text) || text is WholeImage or FixedValue or SameAsRegion ? null : text.Trim();
}

/// <summary>One step in the editor.</summary>
public abstract partial class StepViewModel(PresetEditorViewModel editor) : ViewModelBase
{
    protected PresetEditorViewModel Editor { get; } = editor;

    [ObservableProperty]
    public partial int Number { get; set; }

    public abstract string Title { get; }

    public abstract PresetStep ToStep();

    [RelayCommand]
    private void Remove() => Editor.Remove(this);

    [RelayCommand]
    private void MoveUp() => Editor.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => Editor.Move(this, 1);

    public static StepViewModel From(PresetStep step, PresetEditorViewModel editor) => step switch
    {
        AutoStep => new AutoStepViewModel(editor),
        AddMaskStep m => new MaskStepViewModel(editor)
        {
            MaskName = m.Name,
            Source = MaskStepViewModel.Sources.FirstOrDefault(c => c.Value == m.Source) ?? MaskStepViewModel.Sources[0],
            StoredComponents = m.Components,
        },
        SetValuesStep v => ValuesStepViewModel.From(v, editor),
        GoalStep g => GoalStepViewModel.From(g, editor),
        CurvesStep c => new CurvesStepViewModel(editor, c),
        _ => new AutoStepViewModel(editor),
    };
}

/// <summary>Point curves saved from a photo (kept as they are; edited in the Tone curve panel, not here).</summary>
public sealed class CurvesStepViewModel(PresetEditorViewModel editor, CurvesStep step) : StepViewModel(editor)
{
    public override string Title => "Point curves (saved from a photo)";

    public override PresetStep ToStep() => step;
}

public sealed class AutoStepViewModel(PresetEditorViewModel editor) : StepViewModel(editor)
{
    public override string Title => "Auto (whole image)";

    public override PresetStep ToStep() => new AutoStep();
}

public sealed partial class MaskStepViewModel(PresetEditorViewModel editor) : StepViewModel(editor)
{
    public static IReadOnlyList<Choice<MaskSource>> Sources { get; } =
    [
        new(MaskSource.Subject, "Select Subject (AI)"),
        new(MaskSource.People, "Select People (AI)"),
        new(MaskSource.Sky, "Select Sky (AI)"),
        new(MaskSource.Background, "Background = not the subject (AI)"),
        new(MaskSource.TopGradient, "Gradient from the top"),
        new(MaskSource.BottomGradient, "Gradient from the bottom"),
        new(MaskSource.CenterRadial, "Radial gradient in the centre"),
        new(MaskSource.Geometry, "Stored shape (saved from a photo)"),
    ];

    public override string Title => "Create mask";

    [ObservableProperty]
    public partial string MaskName { get; set; } = "Subject";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShapeNote))]
    public partial Choice<MaskSource> Source { get; set; } = Sources[0];

    public ImmutableList<MaskComponent> StoredComponents { get; init; } = [];

    /// <summary>Explains the stored shape (it can't be drawn in the editor).</summary>
    public string ShapeNote => Source.Value != MaskSource.Geometry ? ""
        : StoredComponents.Count == 0 ? "No stored shape: save a preset from a photo with masks to get one."
        : $"Stored shape: {string.Join(", ", StoredComponents.Select(c => c.DisplayName))}";

    partial void OnMaskNameChanged(string value) => Editor.RefreshMaskNames();

    public override PresetStep ToStep() => new AddMaskStep
    {
        Name = string.IsNullOrWhiteSpace(MaskName) ? "Mask" : MaskName.Trim(),
        Source = Source.Value,
        Components = Source.Value == MaskSource.Geometry ? StoredComponents : [],
    };
}

/// <summary>A slider = value row of a "set values" step.</summary>
public sealed partial class ValueRowViewModel(ValuesStepViewModel owner) : ViewModelBase
{
    [ObservableProperty]
    public partial SliderOption Slider { get; set; } = SliderOption.All[0];

    [ObservableProperty]
    public partial double Value { get; set; }

    [RelayCommand]
    private void Remove() => owner.Rows.Remove(this);
}

public sealed partial class ValuesStepViewModel(PresetEditorViewModel editor) : StepViewModel(editor)
{
    public override string Title => "Set sliders";

    public PresetEditorViewModel Owner => Editor;

    [ObservableProperty]
    public partial string Target { get; set; } = PresetEditorViewModel.WholeImage;

    /// <summary>Add to the current values instead of replacing them.</summary>
    [ObservableProperty]
    public partial bool Relative { get; set; }

    public ObservableCollection<ValueRowViewModel> Rows { get; } = [];

    [RelayCommand]
    private void AddRow() => Rows.Add(new ValueRowViewModel(this));

    public static ValuesStepViewModel From(SetValuesStep step, PresetEditorViewModel editor)
    {
        var vm = new ValuesStepViewModel(editor) { Target = step.Mask ?? PresetEditorViewModel.WholeImage, Relative = step.Relative };
        foreach (var (id, value) in step.Values)
            vm.Rows.Add(new ValueRowViewModel(vm) { Slider = SliderOption.Find(id), Value = value });
        return vm;
    }

    public override PresetStep ToStep()
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, double>();
        foreach (var row in Rows)
            values[row.Slider.Id] = row.Value;
        return new SetValuesStep { Mask = PresetEditorViewModel.MaskOrNull(Target), Relative = Relative, Values = values.ToImmutable() };
    }
}

public sealed partial class GoalStepViewModel(PresetEditorViewModel editor) : StepViewModel(editor)
{
    public static IReadOnlyList<Choice<GoalMetric>> Metrics { get; } =
    [
        new(GoalMetric.Brightness, "brightness"), new(GoalMetric.Saturation, "saturation"),
        new(GoalMetric.Warmth, "warmth"), new(GoalMetric.Contrast, "contrast"),
    ];

    public static IReadOnlyList<Choice<GoalRelation>> Relations { get; } =
    [
        new(GoalRelation.AtLeast, "at least"), new(GoalRelation.AtMost, "at most"), new(GoalRelation.About, "about"),
    ];

    public override string Title => "Goal";

    public PresetEditorViewModel Owner => Editor;

    [ObservableProperty]
    public partial string RegionMask { get; set; } = PresetEditorViewModel.WholeImage;

    [ObservableProperty]
    public partial bool RegionOutside { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetUnit))]
    public partial Choice<GoalMetric> Metric { get; set; } = Metrics[0];

    [ObservableProperty]
    public partial Choice<GoalRelation> Relation { get; set; } = Relations[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetUnit))]
    [NotifyPropertyChangedFor(nameof(HasReference))]
    public partial string Reference { get; set; } = PresetEditorViewModel.WholeImage;

    [ObservableProperty]
    public partial bool ReferenceOutside { get; set; }

    [ObservableProperty]
    public partial double Target { get; set; } = 15;

    [ObservableProperty]
    public partial SliderOption FixBy { get; set; } = SliderOption.Find("exposure");

    [ObservableProperty]
    public partial string FixMask { get; set; } = PresetEditorViewModel.SameAsRegion;

    [ObservableProperty]
    public partial double MaxChange { get; set; } = 1.5;

    [ObservableProperty]
    public partial double Tolerance { get; set; } = 1;

    public bool HasReference => Reference != PresetEditorViewModel.FixedValue;

    /// <summary>How <see cref="Target"/> is read.</summary>
    public string TargetUnit => !HasReference
        ? Metric.Value switch
        {
            GoalMetric.Warmth => "(-100 blue … +100 warm)",
            _ => "(0 … 100)",
        }
        : Metric.Value == GoalMetric.Warmth ? "points more than the reference" : "% more than the reference (negative = less)";

    public static GoalStepViewModel From(GoalStep g, PresetEditorViewModel editor) => new(editor)
    {
        RegionMask = g.Region.Mask ?? PresetEditorViewModel.WholeImage,
        RegionOutside = g.Region.Outside,
        Metric = Metrics.First(m => m.Value == g.Metric),
        Relation = Relations.First(r => r.Value == g.Relation),
        Reference = g.Reference is null ? PresetEditorViewModel.FixedValue : g.Reference.Mask ?? PresetEditorViewModel.WholeImage,
        ReferenceOutside = g.Reference?.Outside ?? false,
        Target = g.Target,
        FixBy = SliderOption.Find(g.FixBy),
        FixMask = g.FixWholeImage ? PresetEditorViewModel.WholeImage : g.FixMask ?? PresetEditorViewModel.SameAsRegion,
        MaxChange = g.MaxChange,
        Tolerance = g.Tolerance,
    };

    public override PresetStep ToStep()
    {
        var regionMask = PresetEditorViewModel.MaskOrNull(RegionMask);
        var referenceMask = PresetEditorViewModel.MaskOrNull(Reference);
        return new GoalStep
        {
            Region = new Region(regionMask, regionMask is not null && RegionOutside),
            Metric = Metric.Value,
            Relation = Relation.Value,
            Reference = HasReference ? new Region(referenceMask, referenceMask is not null && ReferenceOutside) : null,
            Target = Target,
            FixBy = FixBy.Id,
            FixMask = PresetEditorViewModel.MaskOrNull(FixMask),
            FixWholeImage = FixMask == PresetEditorViewModel.WholeImage,
            MaxChange = Math.Abs(MaxChange),
            Tolerance = Math.Abs(Tolerance),
        };
    }
}
