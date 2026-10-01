using System.Collections.Concurrent;
using PhotoEditor.Core.Jobs;

namespace PhotoEditor.Tests.Jobs;

public sealed class JobQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static BackgroundJob Job(string name, ConcurrentQueue<string> order, ManualResetEventSlim? gate = null,
        string? key = null, Func<JobContext, object?>? body = null) =>
        new(name, ctx =>
        {
            order.Enqueue(name);
            gate?.Wait(Timeout);
            return body?.Invoke(ctx) ?? name + " done";
        }, key);

    [Fact]
    public async Task RunsJobsOneAtATime_InOrder_UrgentFirst()
    {
        using var queue = new JobQueue();
        var order = new ConcurrentQueue<string>();
        var gate = new ManualResetEventSlim();
        var first = queue.Enqueue(Job("first", order, gate));
        await Task.Run(() => SpinWait.SpinUntil(() => first.State == JobState.Running, Timeout));
        var a = queue.Enqueue(Job("a", order));
        var b = queue.Enqueue(Job("b", order));
        var urgent = queue.Enqueue(Job("urgent", order), urgent: true);
        await Task.Delay(50);
        Assert.Equal(["first"], order.ToArray()); // only one runs
        gate.Set();
        Assert.Equal("b done", await b.Completion.WaitAsync(Timeout));
        Assert.Equal(["first", "urgent", "a", "b"], order.ToArray());
        Assert.All(new[] { first, a, b, urgent }, j => Assert.Equal(JobState.Done, j.State));
    }

    [Fact]
    public async Task SameKey_ReturnsTheQueuedJob_AndUrgentMovesItForward()
    {
        using var queue = new JobQueue();
        var order = new ConcurrentQueue<string>();
        var gate = new ManualResetEventSlim();
        var blocker = queue.Enqueue(Job("blocker", order, gate));
        await Task.Run(() => SpinWait.SpinUntil(() => blocker.State == JobState.Running, Timeout));
        var export = queue.Enqueue(Job("export", order));
        var denoise = queue.Enqueue(Job("denoise", order, key: "denoise:x"));
        var again = queue.Enqueue(Job("denoise again", order, key: "denoise:x"), urgent: true);
        Assert.Same(denoise, again);
        gate.Set();
        await export.Completion.WaitAsync(Timeout);
        Assert.Equal(["blocker", "denoise", "export"], order.ToArray());

        // Finished jobs don't count: the same key runs again.
        var later = queue.Enqueue(Job("denoise later", order, key: "denoise:x"));
        Assert.NotSame(denoise, later);
        await later.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Cancel_StopsARunningJob_AndSkipsAWaitingOne()
    {
        using var queue = new JobQueue();
        var order = new ConcurrentQueue<string>();
        var started = new ManualResetEventSlim();
        var running = queue.Enqueue(new BackgroundJob("long", ctx =>
        {
            started.Set();
            while (true)
            {
                ctx.Cancel.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        }));
        var waiting = queue.Enqueue(Job("waiting", order));
        var after = queue.Enqueue(Job("after", order));
        Assert.True(started.Wait(Timeout));
        waiting.Cancel();
        Assert.Equal(JobState.Cancelled, waiting.State);
        running.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Completion.WaitAsync(Timeout));
        await after.Completion.WaitAsync(Timeout);
        Assert.Equal(JobState.Cancelled, running.State);
        Assert.Equal(["after"], order.ToArray());
    }

    [Fact]
    public async Task AFailingJob_ReportsItsError_AndTheQueueGoesOn()
    {
        using var queue = new JobQueue();
        var order = new ConcurrentQueue<string>();
        var bad = queue.Enqueue(new BackgroundJob("bad", _ => throw new IOException("disk full")));
        var good = queue.Enqueue(Job("good", order));
        await Assert.ThrowsAsync<IOException>(() => bad.Completion.WaitAsync(Timeout));
        Assert.Equal("good done", await good.Completion.WaitAsync(Timeout));
        Assert.Equal(JobState.Failed, bad.State);
        Assert.Equal("disk full", bad.Error);
    }

    [Fact]
    public async Task ProgressAndStateChanges_AreReported()
    {
        using var queue = new JobQueue();
        var events = new ConcurrentQueue<(JobState, double?)>();
        queue.Changed += j => events.Enqueue((j.State, j.Progress));
        var job = queue.Enqueue(new BackgroundJob("p", ctx =>
        {
            ctx.Report(0.5, "half");
            return null;
        }));
        await job.Completion.WaitAsync(Timeout);
        await Task.Delay(20);
        Assert.Contains((JobState.Waiting, (double?)null), events);
        Assert.Contains((JobState.Running, (double?)0.5), events);
        Assert.Contains((JobState.Done, (double?)1), events);
        Assert.Empty(queue.Jobs); // finished jobs are let go
    }
}
