using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;

namespace PhotoEditor.Core.Presets;

/// <summary>Creating presets from an edit, and the built-in examples.</summary>
public static class PresetFactory
{
    /// <summary>Slider ids per copy group (same groups as "Copy settings").</summary>
    public static IReadOnlyList<string> SliderIds(SettingsGroups groups)
    {
        var ids = new List<string>();
        void Add(params AdjustmentParameter[] ps) => ids.AddRange(ps.Select(p => p.Id));
        if (groups.HasFlag(SettingsGroups.Light))
            Add(AdjustmentParameters.Exposure, AdjustmentParameters.Contrast, AdjustmentParameters.Highlights,
                AdjustmentParameters.Shadows, AdjustmentParameters.Whites, AdjustmentParameters.Blacks);
        if (groups.HasFlag(SettingsGroups.Color))
            Add(AdjustmentParameters.Temperature, AdjustmentParameters.Tint, AdjustmentParameters.Vibrance, AdjustmentParameters.Saturation);
        if (groups.HasFlag(SettingsGroups.ToneCurve))
            Add([.. AdjustmentParameters.ToneCurveSliders]);
        if (groups.HasFlag(SettingsGroups.Hsl))
            Add([.. AdjustmentParameters.Hsl]);
        if (groups.HasFlag(SettingsGroups.Vignette))
            Add(AdjustmentParameters.VignetteAmount, AdjustmentParameters.VignetteMidpoint,
                AdjustmentParameters.VignetteRoundness, AdjustmentParameters.VignetteFeather);
        if (groups.HasFlag(SettingsGroups.Detail))
            Add([.. AdjustmentParameters.GlobalOnly.Except(AdjustmentParameters.ToneCurveSliders).Except(AdjustmentParameters.TransformSliders)]);
        if (groups.HasFlag(SettingsGroups.Transform))
            Add([.. AdjustmentParameters.TransformSliders]);
        return ids;
    }

    /// <summary>
    /// A preset that reproduces the chosen parts of <paramref name="state"/>: fixed whole-image values, and (with
    /// <see cref="SettingsGroups.Masks"/>) each mask's shape and non-default sliders. Goals can be added in the editor.
    /// </summary>
    public static Preset FromEdit(string name, EditState state, SettingsGroups groups)
    {
        var steps = ImmutableList.CreateBuilder<PresetStep>();
        var ids = SliderIds(groups);
        if (ids.Count > 0)
        {
            steps.Add(new SetValuesStep
            {
                Values = ids.ToImmutableSortedDictionary(id => id, id => AdjustmentParameters.ById(id)!.Get(state.Adjustments)),
            });
        }
        var a = state.Adjustments;
        if (groups.HasFlag(SettingsGroups.ToneCurve) && !(a.Curve.IsLinear && a.CurveRed.IsLinear && a.CurveGreen.IsLinear && a.CurveBlue.IsLinear))
            steps.Add(new CurvesStep { Curve = a.Curve, CurveRed = a.CurveRed, CurveGreen = a.CurveGreen, CurveBlue = a.CurveBlue });
        if (groups.HasFlag(SettingsGroups.Masks))
        {
            foreach (var mask in state.Masks.Where(m => m.Components.Count > 0))
            {
                steps.Add(new AddMaskStep { Name = mask.Name, Source = MaskSource.Geometry, Components = mask.Components });
                var values = AdjustmentParameters.All
                    .Where(p => !AdjustmentParameters.GlobalOnly.Contains(p) && p.Get(mask.Adjustments) != p.DefaultValue)
                    .ToImmutableSortedDictionary(p => p.Id, p => p.Get(mask.Adjustments));
                if (values.Count > 0)
                    steps.Add(new SetValuesStep { Mask = mask.Name, Values = values });
            }
        }
        return new Preset { Name = name, Steps = steps.ToImmutable() };
    }

    /// <summary>Examples shipped with the app.</summary>
    public static IReadOnlyList<Preset> BuiltIn { get; } =
    [
        new Preset
        {
            Name = "Portrait – subject pops (AI)",
            IsBuiltIn = true,
            Steps =
            [
                new AutoStep(),
                new AddMaskStep { Name = "Subject", Source = MaskSource.Subject },
                new GoalStep
                {
                    Region = new Region("Subject"), Metric = GoalMetric.Brightness, Relation = GoalRelation.AtLeast,
                    Reference = new Region("Subject", Outside: true), Target = 15, FixBy = "exposure", MaxChange = 1.5,
                },
                new SetValuesStep { Mask = "Subject", Relative = true, Values = ImmutableSortedDictionary<string, double>.Empty.Add("temperature", 5).Add("shadows", 10) },
            ],
        },
        new Preset
        {
            Name = "Landscape – calmer sky (gradient)",
            IsBuiltIn = true,
            Steps =
            [
                new AutoStep(),
                new AddMaskStep { Name = "Sky", Source = MaskSource.TopGradient },
                new GoalStep
                {
                    Region = new Region("Sky"), Metric = GoalMetric.Brightness, Relation = GoalRelation.AtMost,
                    Reference = Region.WholeImage, Target = 10, FixBy = "exposure", MaxChange = 1.5,
                },
                new GoalStep
                {
                    Region = new Region("Sky"), Metric = GoalMetric.Saturation, Relation = GoalRelation.AtLeast,
                    Reference = Region.WholeImage, Target = 10, FixBy = "saturation", MaxChange = 40,
                },
            ],
        },
        new Preset
        {
            Name = "Landscape – dramatic sky (AI)",
            IsBuiltIn = true,
            Steps =
            [
                new AutoStep(),
                new AddMaskStep { Name = "Sky", Source = MaskSource.Sky },
                new GoalStep
                {
                    Region = new Region("Sky"), Metric = GoalMetric.Brightness, Relation = GoalRelation.AtMost,
                    Reference = new Region("Sky", Outside: true), Target = 5, FixBy = "exposure", MaxChange = 1.5,
                },
                new SetValuesStep { Mask = "Sky", Relative = true, Values = ImmutableSortedDictionary<string, double>.Empty.Add("contrast", 20).Add("highlights", -30).Add("saturation", 15) },
            ],
        },
    ];
}

/// <summary>User presets as JSON files in a folder (default: <c>%APPDATA%\PhotoEditor\presets</c>).</summary>
public sealed class PresetStore(string directory)
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoEditor", "presets");

    public string Directory { get; } = directory;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(Preset preset) => JsonSerializer.Serialize(preset, Options);

    /// <summary>Parses a preset; throws <see cref="JsonException"/> on invalid JSON.</summary>
    public static Preset Deserialize(string json)
    {
        var preset = JsonSerializer.Deserialize<Preset>(json, Options) ?? new Preset();
        return preset with { Name = string.IsNullOrWhiteSpace(preset.Name) ? "Preset" : preset.Name, Steps = (preset.Steps ?? []).RemoveAll(s => s is null) };
    }

    /// <summary>File name for a preset name (characters not allowed in file names are replaced).</summary>
    public string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(Directory, (safe.Length == 0 ? "preset" : safe) + ".json");
    }

    /// <summary>All readable presets, sorted by name; unreadable files are reported in <paramref name="errors"/>.</summary>
    public IReadOnlyList<Preset> LoadAll(out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var presets = new List<Preset>();
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    presets.Add(Deserialize(File.ReadAllText(file)));
                }
                catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        errors = problems;
        return presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Saves (or overwrites) the preset under its name.</summary>
    public void Save(Preset preset)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(preset.Name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(preset));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
            File.Delete(path);
    }
}
