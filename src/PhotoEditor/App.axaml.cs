using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoEditor.ViewModels;
using PhotoEditor.Views;

namespace PhotoEditor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            // Allow "PhotoEditor.exe image.jpg" (e.g. "Open with" on Windows) or "PhotoEditor.exe folder".
            if (desktop.Args is [var path, ..])
                _ = System.IO.Directory.Exists(path) ? viewModel.OpenFolderAsync(path) : viewModel.OpenFileAsync(path);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}