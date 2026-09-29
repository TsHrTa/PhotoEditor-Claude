using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Imaging;
using PhotoEditor.ViewModels;

namespace PhotoEditor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddKeyBinding(Key.O, new AsyncRelayCommand(OpenAsync));
        AddKeyBinding(Key.E, new AsyncRelayCommand(ExportAsync));
        AddKeyBinding(Key.S, new RelayCommand(() => ViewModel?.SaveEdits()));
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ViewModel?.SaveEdits(); // don't lose an auto-save that is still pending
        base.OnClosing(e);
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();

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
        if (path is not null)
            ViewModel?.OpenFile(path);
    }

    private static string? FirstImagePath(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && ImageLoader.IsSupported(p));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = FirstImagePath(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (FirstImagePath(e) is { } path)
            ViewModel?.OpenFile(path);
        e.Handled = true;
    }
}
