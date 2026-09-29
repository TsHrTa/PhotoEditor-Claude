using PhotoEditor.Core.Editing;

namespace PhotoEditor.Tests.Editing;

public class EditHistoryTests
{
    private DateTime _now = new(2026, 1, 1);

    private EditHistory<string> Create(int capacity = 100) =>
        new("a", TimeSpan.FromSeconds(1), () => _now, capacity);

    [Fact]
    public void UndoRedo_WalksThroughStates()
    {
        var h = Create();
        h.Record("b");
        h.Record("c");
        Assert.Equal("b", h.Undo());
        Assert.Equal("a", h.Undo());
        Assert.False(h.CanUndo);
        Assert.Equal("a", h.Undo());
        Assert.Equal("b", h.Redo());
        Assert.Equal("c", h.Redo());
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Record_AfterUndo_DropsRedo()
    {
        var h = Create();
        h.Record("b");
        h.Record("c");
        h.Undo();
        h.Record("d");
        Assert.False(h.CanRedo);
        Assert.Equal("b", h.Undo());
    }

    [Fact]
    public void SameKeyWithinWindow_Coalesces()
    {
        var h = Create();
        h.Record("b1", "exposure");
        _now += TimeSpan.FromMilliseconds(300);
        h.Record("b2", "exposure");
        _now += TimeSpan.FromMilliseconds(300);
        h.Record("b3", "exposure");
        Assert.Equal(2, h.Count);
        Assert.Equal("a", h.Undo());
    }

    [Fact]
    public void DifferentKeyOrPause_StartsNewStep()
    {
        var h = Create();
        h.Record("b", "exposure");
        h.Record("c", "contrast");
        _now += TimeSpan.FromSeconds(5);
        h.Record("d", "contrast");
        h.Record("e");
        h.Record("f");
        Assert.Equal(6, h.Count);
    }

    [Fact]
    public void UndoBreaksCoalescing()
    {
        var h = Create();
        h.Record("b", "x");
        h.Record("c", "y");
        h.Undo();
        h.Record("d", "y");
        Assert.Equal("b", h.Undo());
    }

    [Fact]
    public void RecordingCurrentState_IsIgnored()
    {
        var h = Create();
        h.Record("a");
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Capacity_DropsOldestStates()
    {
        var h = Create(capacity: 3);
        h.Record("b");
        h.Record("c");
        h.Record("d");
        Assert.Equal(3, h.Count);
        h.Undo();
        Assert.Equal("b", h.Undo());
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var h = Create();
        h.Record("b");
        h.Reset("z");
        Assert.Equal("z", h.Current);
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }
}
