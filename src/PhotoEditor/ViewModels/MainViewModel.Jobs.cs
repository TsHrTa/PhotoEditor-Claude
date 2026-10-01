using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoEditor.Core.Jobs;

namespace PhotoEditor.ViewModels;

/// <summary>
/// The background job queue (AI denoise / deblur, presets on other photos, exports): one job at a time, the
/// photo on screen first. Jobs keep running when you move to another photo.
/// </summary>
public partial class MainViewModel
{
    private JobQueue? _queue;

    private JobQueue Queue
    {
        get
        {
            if (_queue is null)
            {
                _queue = new JobQueue();
                _queue.Changed += job => Dispatcher.UIThread.Post(() => OnJobChanged(job));
            }
            return _queue;
        }
    }

    /// <summary>The queue panel's rows: unfinished jobs and the finished ones until cleared.</summary>
    public ObservableCollection<JobViewModel> JobList { get; } = [];

    /// <summary>E.g. "AI Denoise – IMG_0001.CR3: 45 % · 3 waiting" (empty when idle).</summary>
    [ObservableProperty]
    public partial string JobSummary { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasJobs { get; private set; }

    /// <summary>Label of the queue button, e.g. "Jobs (3)".</summary>
    [ObservableProperty]
    public partial string JobButtonText { get; private set; } = "Jobs";

    /// <summary>Queues a job; <paramref name="urgent"/> jobs (for the photo on screen) go before the waiting ones.</summary>
    private BackgroundJob Enqueue(BackgroundJob job, bool urgent = false)
    {
        var queued = Queue.Enqueue(job, urgent);
        if (JobList.All(j => j.Job != queued))
        {
            var row = new JobViewModel(queued);
            row.Refresh();
            JobList.Add(row);
        }
        UpdateJobSummary();
        return queued;
    }

    private void OnJobChanged(BackgroundJob job)
    {
        JobList.FirstOrDefault(j => j.Job == job)?.Refresh();
        UpdateJobSummary();
    }

    private void UpdateJobSummary()
    {
        var active = JobList.Where(j => j.IsActive).ToList();
        var running = active.FirstOrDefault(j => j.IsRunning);
        int waiting = active.Count(j => !j.IsRunning);
        JobSummary = running is null && waiting == 0
            ? ""
            : (running is null ? "" : $"{running.Title}: {running.StateText}") + (waiting > 0 ? $" · {waiting} waiting" : "");
        HasJobs = JobList.Count > 0;
        JobButtonText = active.Count > 0 ? $"Jobs ({active.Count})" : "Jobs";
        CancelAllJobsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasActiveJobs))]
    private void CancelAllJobs() => Queue.CancelAll();

    private bool HasActiveJobs() => JobList.Any(j => j.IsActive);

    [RelayCommand]
    private void ClearFinishedJobs()
    {
        foreach (var row in JobList.Where(j => !j.IsActive).ToList())
            JobList.Remove(row);
        UpdateJobSummary();
    }

    /// <summary>Runs <paramref name="then"/> on the UI thread once <paramref name="job"/> has finished (whatever the outcome).</summary>
    private static async Task AfterJobAsync(BackgroundJob job, Action then)
    {
        try
        {
            await job.Completion;
        }
        catch (Exception)
        {
            // The queue panel shows failures and cancellations.
        }
        then();
    }
}
