using System;
using PhotoEditor.Core.Editing;

namespace PhotoEditor.Controls;

/// <summary>
/// A drag of the crop frame in <see cref="ImageViewer"/>: which part is dragged, and the pointer's start and
/// current positions in normalised image coordinates.
/// </summary>
public sealed class CropEditEventArgs(CropHandle handle, (double X, double Y) from, (double X, double Y) to, EditPhase phase) : EventArgs
{
    public CropHandle Handle { get; } = handle;
    public (double X, double Y) From { get; } = from;
    public (double X, double Y) To { get; } = to;
    public EditPhase Phase { get; } = phase;
}
