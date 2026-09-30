using System;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Controls;

public enum SpotEditKind
{
    /// <summary>A click on the photo outside any spot: add a spot there.</summary>
    Add,

    /// <summary>The spot's circle is dragged (<see cref="SpotEditEventArgs.Point"/> = its new centre).</summary>
    Move,

    /// <summary>The spot's source circle is dragged (<see cref="SpotEditEventArgs.Point"/> = the source's new centre).</summary>
    MoveSource,

    /// <summary>A click on a spot that can't be dragged (a Remove spot): select it.</summary>
    Select,
}

/// <summary>Spot removal tool input from <see cref="ImageViewer"/>, in normalised full-image coordinates.</summary>
public sealed class SpotEditEventArgs(SpotEditKind kind, Guid? spot, BrushPoint point, EditPhase phase) : EventArgs
{
    public SpotEditKind Kind { get; } = kind;
    public Guid? Spot { get; } = spot;
    public BrushPoint Point { get; } = point;
    public EditPhase Phase { get; } = phase;
}
