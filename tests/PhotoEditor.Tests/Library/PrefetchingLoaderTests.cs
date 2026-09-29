using System.Collections.Concurrent;
using PhotoEditor.Core.Library;

namespace PhotoEditor.Tests.Library;

public sealed class PrefetchingLoaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A loader whose loads can be held back; records the order of loads.</summary>
    private sealed class FakeLoads
    {
        public ConcurrentQueue<string> Order { get; } = new();
        public ConcurrentDictionary<string, ManualResetEventSlim> Gates { get; } = new();
        public ConcurrentDictionary<string, ManualResetEventSlim> Started { get; } = new();

        public string Load(string path)
        {
            Order.Enqueue(path);
            Started.GetOrAdd(path, _ => new ManualResetEventSlim()).Set();
            if (Gates.TryGetValue(path, out var gate))
                gate.Wait(Timeout);
            if (path.StartsWith("bad"))
                throw new InvalidDataException("corrupt " + path);
            return "pixels of " + path;
        }

        public void Hold(string path) => Gates[path] = new ManualResetEventSlim();
        public void Release(string path) => Gates[path].Set();
        public void WaitStarted(string path) =>
            Assert.True(Started.GetOrAdd(path, _ => new ManualResetEventSlim()).Wait(Timeout), $"{path} never started");
    }

    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(Timeout);

    [Fact]
    public async Task LoadsTheWantedPhoto_ThenPrefetches_ThenServesFromMemory()
    {
        var loads = new FakeLoads();
        using var loader = new PrefetchingLoader<string>(loads.Load);
        Assert.Equal("pixels of a", await Within(loader.Request("a", ["b", "c"])));
        loads.WaitStarted("c");
        await Within(Task.Run(async () => { while (!loader.TryGet("c", out _)) await Task.Delay(5); return 0; }));

        Assert.Equal("pixels of b", await Within(loader.Request("b", ["c", "a"])));
        Assert.Equal(["a", "b", "c"], loads.Order.ToArray()); // nothing loaded twice
    }

    [Fact]
    public async Task ANewerRequest_DropsWaitingOnes_ButKeepsTheRunningLoad()
    {
        var loads = new FakeLoads();
        loads.Hold("a");
        using var loader = new PrefetchingLoader<string>(loads.Load);
        var a = loader.Request("a", []);
        loads.WaitStarted("a");
        var b = loader.Request("b", []);
        var c = loader.Request("c", []);
        Assert.Null(await Within(b)); // replaced before it started
        loads.Release("a");
        Assert.Equal("pixels of a", await Within(a)); // was already running: finished and kept
        Assert.Equal("pixels of c", await Within(c));
        Assert.DoesNotContain("b", loads.Order);
        Assert.True(loader.TryGet("a", out _));
    }

    [Fact]
    public async Task KeepsOnlyTheMostRecentlyUsed()
    {
        var loads = new FakeLoads();
        using var loader = new PrefetchingLoader<string>(loads.Load, capacity: 2);
        await Within(loader.Request("a", []));
        await Within(loader.Request("b", []));
        await Within(loader.Request("a", [])); // a is used again
        await Within(loader.Request("c", []));
        Assert.True(loader.TryGet("a", out _));
        Assert.False(loader.TryGet("b", out _));
        Assert.True(loader.TryGet("c", out _));
    }

    [Fact]
    public async Task ALoadError_GoesToTheCaller_AndIsRetriedOnTheNextRequest()
    {
        var loads = new FakeLoads();
        using var loader = new PrefetchingLoader<string>(loads.Load);
        await Assert.ThrowsAsync<InvalidDataException>(() => Within(loader.Request("bad", ["ok"])));
        Assert.Equal("pixels of ok", await Within(loader.Request("ok", [])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Within(loader.Request("bad", [])));
        Assert.Equal(2, loads.Order.Count(p => p == "bad"));
    }

    [Fact]
    public async Task AFailedPrefetch_IsNotRetriedInALoop()
    {
        var loads = new FakeLoads();
        using var loader = new PrefetchingLoader<string>(loads.Load);
        await Within(loader.Request("a", ["bad"]));
        loads.WaitStarted("bad");
        await Task.Delay(100);
        Assert.Equal(1, loads.Order.Count(p => p == "bad"));
    }
}
