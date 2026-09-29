namespace PhotoEditor.Core.Editing;

/// <summary>
/// Undo/redo stack of immutable edit states. Consecutive changes with the same coalesce key
/// (e.g. one slider being dragged) within <see cref="CoalesceWindow"/> become a single step.
/// </summary>
public sealed class EditHistory<T> where T : class
{
    public const int DefaultCapacity = 500;

    private readonly List<T> _states = [];
    private readonly Func<DateTime> _clock;
    private readonly int _capacity;
    private int _index;
    private string? _lastKey;
    private DateTime _lastTime;

    public EditHistory(T initial, TimeSpan? coalesceWindow = null, Func<DateTime>? clock = null, int capacity = DefaultCapacity)
    {
        CoalesceWindow = coalesceWindow ?? TimeSpan.FromSeconds(1);
        _clock = clock ?? (() => DateTime.UtcNow);
        _capacity = Math.Max(2, capacity);
        _states.Add(initial);
    }

    public TimeSpan CoalesceWindow { get; }

    public T Current => _states[_index];
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _states.Count - 1;

    /// <summary>Number of states (including the initial one) on the stack.</summary>
    public int Count => _states.Count;

    /// <summary>Records a new state; drops any redo states.</summary>
    public void Record(T state, string? coalesceKey = null)
    {
        if (Equals(state, Current))
            return;

        var now = _clock();
        bool coalesce = coalesceKey is not null && coalesceKey == _lastKey && _index > 0
            && now - _lastTime <= CoalesceWindow && !CanRedo;

        _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        if (coalesce)
        {
            _states[_index] = state;
        }
        else
        {
            _states.Add(state);
            _index++;
            if (_states.Count > _capacity)
            {
                _states.RemoveAt(0);
                _index--;
            }
        }
        _lastKey = coalesceKey;
        _lastTime = now;
    }

    public T Undo()
    {
        if (CanUndo)
            _index--;
        _lastKey = null;
        return Current;
    }

    public T Redo()
    {
        if (CanRedo)
            _index++;
        _lastKey = null;
        return Current;
    }

    /// <summary>Clears the history and starts again from <paramref name="state"/> (e.g. a new image).</summary>
    public void Reset(T state)
    {
        _states.Clear();
        _states.Add(state);
        _index = 0;
        _lastKey = null;
    }
}
