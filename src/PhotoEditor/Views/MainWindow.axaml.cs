using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Controls;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Imaging;
using PhotoEditor.ViewModels;

namespace PhotoEditor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddKeyBinding(Key.O, new AsyncRelayCommand(OpenAsync));
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new AsyncRelayCommand(OpenFolderAsync),
        });
        // Arrow keys move through the folder. Handled before focus navigation (which would take them otherwise).
        AddHandler(KeyDownEvent, OnArrowKey, RoutingStrategies.Tunnel);
        AddKeyBinding(Key.E, new AsyncRelayCommand(ExportAsync));
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new AsyncRelayCommand(BatchExportAsync),
        });
        AddKeyBinding(Key.S, new RelayCommand(() => ViewModel?.SaveEdits()));
        AddKeyBinding(Key.U, new RelayCommand(() => ViewModel?.AutoCommand.Execute(null)));
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(() => { if (ViewModel?.HasImage == true) CopyButton.Flyout?.ShowAt(CopyButton); }),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(() => ViewModel?.PasteSettingsCommand.Execute(null)),
        });
        AddKeyBinding(Key.OemOpenBrackets, new RelayCommand(() => ViewModel?.RotateLeftCommand.Execute(null)));
        AddKeyBinding(Key.OemCloseBrackets, new RelayCommand(() => ViewModel?.RotateRightCommand.Execute(null)));
        AddKeyBinding(Key.Z, new RelayCommand(() => ViewModel?.UndoCommand.Execute(null)));
        AddKeyBinding(Key.Y, new RelayCommand(() => ViewModel?.RedoCommand.Execute(null)));
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(() => ViewModel?.RedoCommand.Execute(null)),
        });
        AddKeyBinding(Key.D0, new RelayCommand(Viewer.ZoomToFit));
        AddKeyBinding(Key.NumPad0, new RelayCommand(Viewer.ZoomToFit));
        AddKeyBinding(Key.D1, new RelayCommand(Viewer.ZoomToActualSize));
        AddKeyBinding(Key.NumPad1, new RelayCommand(Viewer.ZoomToActualSize));
        AddPlainKeyBinding(Key.OemPipe, () => ViewModel?.ToggleBeforeAfterCommand.Execute(null));
        AddPlainKeyBinding(Key.OemBackslash, () => ViewModel?.ToggleBeforeAfterCommand.Execute(null));
        AddPlainKeyBinding(Key.O, () => ViewModel?.ToggleMaskOverlayCommand.Execute(null));
        // handledEventsToo: the slider thumb handles the pointer itself.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Viewer.BrushStroke += OnBrushStroke;
        Viewer.ComponentEdit += OnComponentEdit;
        Viewer.CropEdit += OnCropEdit;
        Viewer.SpotEdit += (_, e) => ViewModel?.EditSpot(e.Kind, e.Spot, e.Point, e.Phase);
        Viewer.SpotPainted += async (_, path) => { if (ViewModel is { } vm) await vm.PaintedAsync(path); };
        AddPlainKeyBinding(Key.Q, () => { if (ViewModel is { } vm) vm.IsSpotActive = !vm.IsSpotActive; });
        AddPlainKeyBinding(Key.Delete, () => { if (ViewModel is { IsSpotActive: true } vm) vm.DeleteSpotCommand.Execute(null); });
        AddPlainKeyBinding(Key.Back, () => { if (ViewModel is { IsSpotActive: true } vm) vm.DeleteSpotCommand.Execute(null); });
        Viewer.ObjectSelect += async (_, e) => { if (ViewModel is { } vm) await vm.SelectObjectAsync(e.Point, e.Box); };
        AddPlainKeyBinding(Key.S, () => { if (ViewModel is { } vm) vm.IsObjectSelectActive = !vm.IsObjectSelectActive; });
        AddPlainKeyBinding(Key.B, () => { if (ViewModel is { } vm) vm.IsBrushActive = !vm.IsBrushActive; });
        AddPlainKeyBinding(Key.L, () => { if (ViewModel is { } vm) vm.IsLinearGradientActive = !vm.IsLinearGradientActive; });
        AddPlainKeyBinding(Key.R, () => { if (ViewModel is { } vm) vm.IsRadialGradientActive = !vm.IsRadialGradientActive; });
        AddPlainKeyBinding(Key.C, () => { if (ViewModel is { } vm) vm.IsCropActive = !vm.IsCropActive; });
        // X swaps the crop orientation while cropping, otherwise rejects the photo (as in Lightroom).
        AddPlainKeyBinding(Key.X, () =>
        {
            if (ViewModel is { IsCropActive: true } vm)
                vm.SwapCropOrientationCommand.Execute(null);
            else
                ViewModel?.SetFlag(PhotoFlag.Reject);
        });
        AddPlainKeyBinding(Key.P, () => ViewModel?.SetFlag(PhotoFlag.Pick));
        AddPlainKeyBinding(Key.U, () => ViewModel?.SetFlag(PhotoFlag.None));
        for (int stars = 0; stars <= 5; stars++)
        {
            int rating = stars;
            AddPlainKeyBinding(Key.D0 + stars, () => ViewModel?.SetRating(rating));
            AddPlainKeyBinding(Key.NumPad0 + stars, () => ViewModel?.SetRating(rating));
        }
        AddPlainKeyBinding(Key.Enter, () => { if (ViewModel is { IsCropActive: true } vm) vm.ActiveTool = EditTool.None; });
        AddPlainKeyBinding(Key.Escape, () => { if (ViewModel is { } vm) vm.ActiveTool = EditTool.None; });
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void AddKeyBinding(Key key, System.Windows.Input.ICommand command) =>
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, KeyModifiers.Control), Command = command });

    /// <summary>Shortcut without modifiers; ignored while typing in a text box.</summary>
    private void AddPlainKeyBinding(Key key, Action action) =>
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(key),
            Command = new RelayCommand(() =>
            {
                if (FocusManager?.GetFocusedElement() is not TextBox)
                    action();
            }),
        });

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>← / → open the previous / next photo, unless a control that uses arrow keys has the focus.</summary>
    private void OnArrowKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right) || ViewModel is not { } vm)
            return;
        for (var v = FocusManager?.GetFocusedElement() as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is TextBox or Slider or ListBox or ComboBox or NumericUpDown)
                return;
        }
        var command = e.Key == Key.Right ? vm.NextPhotoCommand : vm.PreviousPhotoCommand;
        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ViewModel?.SaveEdits(); // don't lose an auto-save that is still pending
        base.OnClosing(e);
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e) => await OpenFolderAsync();

    /// <summary>A star button: sets that many stars, or clears the rating when it is already set.</summary>
    private void OnStarClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int stars) && ViewModel is { } vm)
            vm.SetRating(vm.CurrentPhoto?.Labels.Rating == stars ? 0 : stars);
    }

    private void OnPickClick(object? sender, RoutedEventArgs e) => ViewModel?.SetFlag(PhotoFlag.Pick);

    private void OnRejectClick(object? sender, RoutedEventArgs e) => ViewModel?.SetFlag(PhotoFlag.Reject);

    private async Task OpenFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder && ViewModel is { } vm)
            await vm.OpenFolderAsync(folder);
    }

    /// <summary>Double-clicking a slider (or its label) resets that adjustment.</summary>
    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is StyledElement { DataContext: ParameterViewModel parameter })
            {
                parameter.Reset();
                e.Handled = true;
                return;
            }
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e) => await ExportAsync();

    private async void OnBatchExportClick(object? sender, RoutedEventArgs e) => await BatchExportAsync();

    private async Task BatchExportAsync()
    {
        if (ViewModel is not { CanBatchExport: true } vm)
            return;
        var dialog = vm.CreateBatchExport();
        if (await new BatchExportWindow { DataContext = dialog }.ShowDialog<bool>(this))
            vm.StartBatchExport(dialog);
    }

    private async Task ExportAsync()
    {
        if (ViewModel is not { CanExport: true } vm)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export image",
            SuggestedFileName = vm.SuggestedExportName,
            DefaultExtension = "jpg",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
                new FilePickerFileType("PNG") { Patterns = ["*.png"] },
            ],
        });
        if (file?.TryGetLocalPath() is { } path)
            await vm.ExportAsync(path);
    }

    private void OnBrushStroke(object? sender, BrushStrokeEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        switch (e.Phase)
        {
            case BrushStrokePhase.Begin:
                vm.BeginStroke(e.X, e.Y, e.Erase);
                break;
            case BrushStrokePhase.Move:
                vm.ContinueStroke(e.X, e.Y);
                break;
            default:
                vm.EndStroke();
                break;
        }
    }

    private void OnComponentEdit(object? sender, ComponentEditEventArgs e) =>
        ViewModel?.EditComponent(e.Component, e.Phase == EditPhase.Begin, e.Phase == EditPhase.End, e.IsNew);

    private void OnCropEdit(object? sender, CropEditEventArgs e) =>
        ViewModel?.EditCrop(e.Handle, e.From, e.To, e.Phase == EditPhase.Begin);

    private void OnCopySettingsClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CopySettingsCommand.Execute(null);
        CopyButton.Flyout?.Hide();
    }

    private async void OnPasteToPhotosClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { HasCopiedSettings: true } vm)
            return;
        var paths = await PickPhotosAsync("Paste settings into photos");
        if (paths.Count > 0)
            await vm.PasteToFilesAsync(paths);
    }

    private async void OnNewPresetClick(object? sender, RoutedEventArgs e) => await EditPresetAsync(null);

    private async void OnEditPresetClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedPreset is { } preset)
            await EditPresetAsync(preset);
    }

    private async Task EditPresetAsync(PhotoEditor.Core.Presets.Preset? preset)
    {
        if (ViewModel is not { } vm)
            return;
        var editor = vm.CreatePresetEditor(preset);
        var window = new PresetEditorWindow { DataContext = editor };
        bool saved = await window.ShowDialog<bool>(this);
        vm.FinishPresetEditor(editor, saved);
    }

    private void OnSavePresetClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SaveEditAsPresetCommand.Execute(null);
        SavePresetButton.Flyout?.Hide();
    }

    private async void OnApplyPresetToPhotosClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { HasSelectedPreset: true } vm)
            return;
        var paths = await PickPhotosAsync("Apply preset to photos");
        if (paths.Count > 0)
            await vm.ApplyPresetToFilesAsync(paths);
    }

    private async Task<IReadOnlyList<string>> PickPhotosAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ImageLoader.SupportedExtensions.Select(ext => "*" + ext).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => Viewer.ZoomToFit();

    private void OnActualSizeClick(object? sender, RoutedEventArgs e) => Viewer.ZoomToActualSize();

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ImageLoader.SupportedExtensions.Select(ext => "*" + ext).ToArray(),
                },
                FilePickerFileTypes.All,
            ],
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null && ViewModel is { } vm)
            await vm.OpenFileAsync(path);
    }

    /// <summary>The first dropped photo or folder.</summary>
    private static string? FirstDroppedPath(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && (ImageLoader.IsSupported(p) || System.IO.Directory.Exists(p)));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = FirstDroppedPath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (FirstDroppedPath(e) is not { } path || ViewModel is not { } vm)
            return;
        if (System.IO.Directory.Exists(path))
            await vm.OpenFolderAsync(path);
        else
            await vm.OpenFileAsync(path);
    }
}
