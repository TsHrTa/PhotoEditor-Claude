namespace PhotoEditor.Core.Library;

/// <summary>
/// Loads photos one at a time on a background thread: first the one wanted now, then the ones likely needed
/// next (e.g. the neighbours in the filmstrip), keeping the most recently used few in memory. A new request
/// replaces the old wishes; loads that were only waiting and are no longer wanted are dropped (their tasks
/// return null). A load that already started can't be stopped, but its result is kept for later.
/// </summary>
public sealed class PrefetchingLoader<T> : IDisposable where T : class
{
    private readonly Func<string, T> _load;
    private readonly int _capacity;
    private readonly StringComparer _comparer;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Loaded items, most recently used first.</summary>
    private readonly LinkedList<(string Path, T Value)> _cache = new();
    private readonly Dictionary<string, TaskCompletionSource<T?>> _waiters;
    private readonly HashSet<string> _failed;
    private string? _wanted;
    private IReadOnlyList<string> _prefetch = [];
    private string? _loading;
    private Task? _worker;

    /// <param name="load">Loads a photo (runs on the worker thread; exceptions go to the task of whoever wanted it).</param>
    /// <param name="capacity">How many loaded photos to keep (the wanted one is never dropped).</param>
    public PrefetchingLoader(Func<string, T> load, int capacity = 3, StringComparer? comparer = null)
    {
        _load = load;
        _capacity = Math.Max(1, capacity);
        _comparer = comparer ?? (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _waiters = new Dictionary<string, TaskCompletionSource<T?>>(_comparer);
        _failed = new HashSet<string>(_comparer);
    }

    /// <summary>Fired on the worker thread after a photo finished loading (for status displays).</summary>
    public event Action<string>? Loaded;

    /// <summary>
    /// Asks for <paramref name="path"/> now and for <paramref name="prefetch"/> afterwards (in that order).
    /// The task completes with the photo, or null if the request was replaced before its load started.
    /// </summary>
    public Task<T?> Request(string path, IReadOnlyList<string> prefetch)
    {
        TaskCompletionSource<T?> waiter;
        List<TaskCompletionSource<T?>> dropped = [];
        lock (_lock)
        {
            _wanted = path;
            _prefetch = prefetch;
            _failed.Remove(path);
            foreach (var (other, tcs) in _waiters.ToList())
            {
                if (!_comparer.Equals(other, path) && !_comparer.Equals(other, _loading))
                {
                    _waiters.Remove(other);
                    dropped.Add(tcs);
                }
            }
            if (Find(path) is { } node)
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                waiter = new TaskCompletionSource<T?>();
                waiter.SetResult(node.Value.Value);
            }
            else if (!_waiters.TryGetValue(path, out waiter!))
            {
                waiter = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[path] = waiter;
            }
            _worker ??= Task.Run(WorkAsync);
        }
        foreach (var tcs in dropped)
            tcs.TrySetResult(null);
        _signal.Release();
        return waiter.Task;
    }

    /// <summary>The photo if it is already loaded.</summary>
    public bool TryGet(string path, out T? value)
    {
        lock (_lock)
        {
            value = Find(path)?.Value.Value;
            return value is not null;
        }
    }

    private LinkedListNode<(string Path, T Value)>? Find(string path)
    {
        for (var node = _cache.First; node is not null; node = node.Next)
            if (_comparer.Equals(node.Value.Path, path))
                return node;
        return null;
    }

    private async Task WorkAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            string? next;
            lock (_lock)
            {
                next = new[] { _wanted }.Concat(_prefetch)
                    .FirstOrDefault(p => p is not null && Find(p) is null && !_failed.Contains(p));
                _loading = next;
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

            T? value = null;
            Exception? error = null;
            try
            {
                value = _load(next);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            TaskCompletionSource<T?>? waiter;
            lock (_lock)
            {
                _loading = null;
                if (value is not null)
                {
                    _cache.AddFirst((next, value));
                    for (var node = _cache.Last; _cache.Count > _capacity && node is not null;)
                    {
                        var previous = node.Previous;
                        if (!_comparer.Equals(node.Value.Path, _wanted))
                            _cache.Remove(node);
                        node = previous;
                    }
                }
                else
                    _failed.Add(next);
                _waiters.Remove(next, out waiter);
            }
            if (error is not null)
                waiter?.TrySetException(error);
            else
                waiter?.TrySetResult(value);
            if (value is not null)
                Loaded?.Invoke(next);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _signal.Release();
    }
}
