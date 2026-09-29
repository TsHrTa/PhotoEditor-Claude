using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Imaging;
using PhotoEditor.ViewModels;

namespace PhotoEditor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new AsyncRelayCommand(OpenAsync) });
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenAsync();

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
