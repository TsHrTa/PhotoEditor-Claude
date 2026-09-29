using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhotoEditor.Views;

/// <summary>Dialog for editing a preset's steps; closes with true when saved.</summary>
public partial class PresetEditorWindow : Window
{
    public PresetEditorWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
