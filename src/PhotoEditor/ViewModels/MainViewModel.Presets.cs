using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Export;
using PhotoEditor.Core.Presets;

namespace PhotoEditor.ViewModels;

/// <summary>Adaptive presets: list, apply (to the open photo or many files), save the current edit, delete.</summary>
public partial class MainViewModel
{
    private readonly PresetStore _presetStore = new(PresetStore.DefaultDirectory);

    /// <summary>AI mask detection for presets (none until the Phase 4 models exist).</summary>
    private IMaskDetector? MaskDetector => null;

    /// <summary>Built-in presets followed by the user's.</summary>
    public ObservableCollection<Preset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPreset))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedPreset))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    public partial Preset? SelectedPreset { get; set; }

    public bool HasSelectedPreset => SelectedPreset is not null;

    public bool CanDeleteSelectedPreset => SelectedPreset is { IsBuiltIn: false };

    /// <summary>What the last preset did, one line per step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPresetLog))]
    public partial string PresetLog { get; private set; } = "";

    public bool HasPresetLog => PresetLog.Length > 0;

    /// <summary>Name typed in the "Save as preset" flyout.</summary>
    [ObservableProperty]
    public partial string NewPresetName { get; set; } = "My preset";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
    public partial bool IsApplyingPreset { get; private set; }

    /// <summary>Reloads built-in and user presets, keeping the selection by name.</summary>
    public void LoadPresets(string? select = null)
    {
        select ??= SelectedPreset?.Name;
        var user = _presetStore.LoadAll(out var errors);
        Presets.Clear();
        foreach (var preset in PresetFactory.BuiltIn.Concat(user))
            Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == select && !p.IsBuiltIn)
            ?? Presets.FirstOrDefault(p => p.Name == select);
        if (errors.Count > 0)
            Status = $"Some presets could not be read: {string.Join("; ", errors)}";
    }

    /// <summary>Runs the selected preset on the open photo (one undo step).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private async Task ApplyPreset()
    {
        if (SelectedPreset is not { } preset || Original is not { } image)
            return;
        IsApplyingPreset = true;
        Status = $"Applying \"{preset.Name}\"…";
        try
        {
            var start = State;
            var detector = MaskDetector;
            var result = await Task.Run(() => PresetEngine.Apply(preset, start, image, detector));
            SelectedMask = null;
            ApplyEdit(result.State);
            PresetLog = string.Join(Environment.NewLine, result.Log.Select(l => "• " + l));
            Status = $"Applied \"{preset.Name}\".";
        }
        finally
        {
            IsApplyingPreset = false;
        }
    }

    private bool CanApplyPreset() => SelectedPreset is not null && HasImage && !IsApplyingPreset;

    /// <summary>Runs the selected preset on other photos (their sidecars are written; the photos are not changed).</summary>
    public async Task ApplyPresetToFilesAsync(IReadOnlyList<string> paths)
    {
        if (SelectedPreset is not { } preset || paths.Count == 0)
            return;
        var others = paths.Where(p => !ImageExporter.IsSameFile(p, FilePath)).ToList();
        if (others.Count < paths.Count)
            await ApplyPreset();

        var detector = MaskDetector;
        var failed = new List<string>();
        for (int i = 0; i < others.Count; i++)
        {
            Status = $"Applying \"{preset.Name}\"… {i + 1} / {others.Count} ({Path.GetFileName(others[i])})";
            var path = others[i];
            var result = await Task.Run(() => PresetEngine.ApplyToFile(preset, path, detector));
            if (result.Error is not null)
                failed.Add($"{Path.GetFileName(path)} ({result.Error})");
        }
        int done = paths.Count - failed.Count;
        Status = failed.Count == 0
            ? $"Applied \"{preset.Name}\" to {done} photo{(done == 1 ? "" : "s")}."
            : $"Applied to {done} of {paths.Count}; failed: {string.Join(", ", failed)}";
    }

    /// <summary>Saves the current edit (the groups ticked under "Include") as a user preset.</summary>
    [RelayCommand]
    private void SaveEditAsPreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0)
        {
            Status = "Enter a name for the preset.";
            return;
        }
        if (PresetFactory.BuiltIn.Any(p => p.Name == name))
            name += " (copy)";
        var preset = PresetFactory.FromEdit(name, State, SelectedCopyGroups);
        SavePreset(preset);
    }

    /// <summary>Writes a user preset (overwriting one with the same name) and selects it.</summary>
    public void SavePreset(Preset preset)
    {
        try
        {
            bool replaced = Presets.Any(p => p.Name == preset.Name && !p.IsBuiltIn);
            _presetStore.Save(preset with { IsBuiltIn = false });
            LoadPresets(preset.Name);
            Status = $"{(replaced ? "Updated" : "Saved")} preset \"{preset.Name}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save the preset: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedPreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset)
            return;
        try
        {
            _presetStore.Delete(preset.Name);
            LoadPresets();
            Status = $"Deleted preset \"{preset.Name}\".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not delete the preset: {ex.Message}";
        }
    }
}
