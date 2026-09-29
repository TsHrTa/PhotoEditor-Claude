namespace PhotoEditor.Core.Jobs;

public enum JobState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>What a running job can do: report progress and see whether it was cancelled.</summary>
public sealed class JobContext(BackgroundJob job, CancellationToken cancel)
{
    public CancellationToken Cancel { get; } = cancel;

    /// <summary>Reports progress (0..1, null = unknown) and an optional short text such as "about 2 min left".</summary>
    public void Report(double? fraction, string? detail = null) => job.SetProgress(fraction, detail);
}

/// <summary>
/// A unit of background work (AI denoise of one photo, applying a preset to one photo, one export, …).
/// Its properties change on the queue's worker thread; <see cref="JobQueue.Changed"/> reports every change.
/// </summary>
public sealed class BackgroundJob
{
    private readonly Func<JobContext, object?> _run;
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancel = new();
    private JobQueue? _queue;

    /// <param name="title">Shown in the queue, e.g. "AI Denoise – IMG_0001.CR3".</param>
    /// <param name="run">The work; runs on the worker thread and returns a result (e.g. a bitmap) or null.</param>
    /// <param name="key">Jobs with the same key are the same work: queuing it again returns the job already queued.</param>
    /// <param name="photoPath">The photo the job is about (to find its jobs), if any.</param>
    public BackgroundJob(string title, Func<JobContext, object?> run, string? key = null, string? photoPath = null)
    {
        Title = title;
        _run = run;
        Key = key;
        PhotoPath = photoPath;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string? Key { get; }
    public string? PhotoPath { get; }

    public JobState State { get; private set; } = JobState.Waiting;
    public double? Progress { get; private set; }
    public string? Detail { get; private set; }

    /// <summary>The error message of a failed job.</summary>
    public string? Error { get; private set; }

    public bool IsFinished => State is JobState.Done or JobState.Failed or JobState.Cancelled;

    /// <summary>Completes with the job's result; faults with its error; is cancelled when the job is.</summary>
    public Task<object?> Completion => _completion.Task;

    /// <summary>Asks the job to stop (a waiting job never starts).</summary>
    public void Cancel()
    {
        _cancel.Cancel();
        _queue?.OnCancelRequested(this);
    }

    internal void Attach(JobQueue queue) => _queue = queue;

    internal void SetProgress(double? fraction, string? detail)
    {
        Progress = fraction is { } f ? Math.Clamp(f, 0, 1) : null;
        Detail = detail;
        _queue?.RaiseChanged(this);
    }

    /// <summary>Marks a waiting job as cancelled (it was never started).</summary>
    internal void CancelWaiting()
    {
        State = JobState.Cancelled;
        _completion.TrySetCanceled();
    }

    internal void Execute()
    {
        if (_cancel.IsCancellationRequested)
        {
            CancelWaiting();
            return;
        }
        State = JobState.Running;
        _queue?.RaiseChanged(this);
        try
        {
            var result = _run(new JobContext(this, _cancel.Token));
            _cancel.Token.ThrowIfCancellationRequested();
            State = JobState.Done;
            Progress = 1;
            _completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            State = JobState.Cancelled;
            _completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            State = JobState.Failed;
            Error = ex.Message;
            _completion.TrySetException(ex);
        }
    }
}

/// <summary>
/// Runs background jobs one at a time, in order; urgent jobs (e.g. the AI work for the photo on screen) go before
/// the waiting ones. One worker is enough: heavy jobs already use the whole GPU / all CPU cores, so running two
/// at once would not finish sooner and would make the editor sluggish.
/// </summary>
public sealed class JobQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly List<BackgroundJob> _jobs = [];
    private readonly LinkedList<BackgroundJob> _waiting = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    /// <summary>A job was added, started, reported progress or finished (raised on the worker or calling thread).</summary>
    public event Action<BackgroundJob>? Changed;

    /// <summary>The unfinished jobs (running and waiting), oldest first. Finished jobs are let go (a UI keeps its own history).</summary>
    public IReadOnlyList<BackgroundJob> Jobs
    {
        get
        {
            lock (_lock)
                return _jobs.ToList();
        }
    }

    public BackgroundJob? Running
    {
        get
        {
            lock (_lock)
                return _jobs.FirstOrDefault(j => j.State == JobState.Running);
        }
    }

    public int WaitingCount
    {
        get
        {
            lock (_lock)
                return _waiting.Count;
        }
    }

    /// <summary>
    /// Queues <paramref name="job"/>, or returns the unfinished job with the same key (moved to the front when
    /// <paramref name="urgent"/>).
    /// </summary>
    public BackgroundJob Enqueue(BackgroundJob job, bool urgent = false)
    {
        BackgroundJob queued;
        lock (_lock)
        {
            var same = job.Key is null ? null : _jobs.FirstOrDefault(j => !j.IsFinished && j.Key == job.Key);
            queued = same ?? job;
            if (same is null)
            {
                job.Attach(this);
                _jobs.Add(job);
                if (urgent)
                    _waiting.AddFirst(job);
                else
                    _waiting.AddLast(job);
            }
            else if (urgent && _waiting.Remove(same))
                _waiting.AddFirst(same);
            _worker ??= Task.Run(WorkAsync);
        }
        if (ReferenceEquals(queued, job))
        {
            RaiseChanged(job);
            _signal.Release();
        }
        return queued;
    }

    /// <summary>Cancels every unfinished job (optionally only those about one photo).</summary>
    public void CancelAll(Func<BackgroundJob, bool>? which = null)
    {
        foreach (var job in Jobs.Where(j => !j.IsFinished && (which?.Invoke(j) ?? true)))
            job.Cancel();
    }

    internal void OnCancelRequested(BackgroundJob job)
    {
        bool removed;
        lock (_lock)
            removed = _waiting.Remove(job);
        if (removed)
        {
            job.CancelWaiting();
            lock (_lock)
                _jobs.Remove(job);
            RaiseChanged(job);
        }
    }

    internal void RaiseChanged(BackgroundJob job) => Changed?.Invoke(job);

    private async Task WorkAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            BackgroundJob? next;
            lock (_lock)
            {
                next = _waiting.First?.Value;
                if (next is not null)
                    _waiting.RemoveFirst();
            }
            if (next is null)
            {
                try
                {
                    await _signal.WaitAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            next.Execute();
            lock (_lock)
                _jobs.Remove(next);
            RaiseChanged(next);
        }
    }

    public void Dispose()
    {
        CancelAll();
        _stop.Cancel();
    }
}
