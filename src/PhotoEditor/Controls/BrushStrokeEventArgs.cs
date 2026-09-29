using System;

namespace PhotoEditor.Controls;

public enum BrushStrokePhase
{
    Begin,
    Move,
    End,
}

/// <summary>Brush input from <see cref="ImageViewer"/>; X/Y are normalised image coordinates.</summary>
public sealed class BrushStrokeEventArgs(BrushStrokePhase phase, double x, double y, bool erase) : EventArgs
{
    public BrushStrokePhase Phase { get; } = phase;
    public double X { get; } = x;
    public double Y { get; } = y;

    /// <summary>Alt was held when the stroke began: erase instead of paint (or the reverse).</summary>
    public bool Erase { get; } = erase;
}
