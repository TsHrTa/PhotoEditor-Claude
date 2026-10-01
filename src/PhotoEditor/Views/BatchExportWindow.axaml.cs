using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoEditor.ViewModels;

namespace PhotoEditor.Views;

/// <summary>Dialog for exporting several photos; closes with true to start the export.</summary>
public partial class BatchExportWindow : Window
{
    public BatchExportWindow()
    {
        InitializeComponent();
    }

    private BatchExportViewModel? ViewModel => DataContext as BatchExportViewModel;

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export to folder" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder && ViewModel is { } vm)
            vm.Folder = folder;
    }

    private void OnJpegClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.IsPng = false;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
