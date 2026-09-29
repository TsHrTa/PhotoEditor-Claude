using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Jobs;

namespace PhotoEditor.ViewModels;

/// <summary>A row in the job queue panel (updated on the UI thread from the job's changes).</summary>
public partial class JobViewModel(BackgroundJob job) : ViewModelBase
{
    public BackgroundJob Job { get; } = job;
    public string Title => Job.Title;

    [ObservableProperty]
    public partial string StateText { get; private set; } = "Waiting";

    /// <summary>0..100.</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>Copies the job's current state (call on the UI thread).</summary>
    public void Refresh()
    {
        IsRunning = Job.State == JobState.Running;
        IsActive = !Job.IsFinished;
        Percent = (Job.Progress ?? 0) * 100;
        IsIndeterminate = IsRunning && Job.Progress is null;
        StateText = Job.State switch
        {
            JobState.Waiting => "Waiting",
            JobState.Running => Job.Progress is { } p
                ? $"{p:P0}{(Job.Detail is { } d ? " · " + d : "")}"
                : Job.Detail ?? "Running…",
            JobState.Done => "Done",
            JobState.Failed => "Failed: " + Job.Error,
            _ => "Cancelled",
        };
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsActive))]
    private void Cancel() => Job.Cancel();
}
