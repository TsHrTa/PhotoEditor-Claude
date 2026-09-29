using System;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Controls;

public enum EditPhase
{
    Begin,
    Move,
    End,
}

/// <summary>
/// A gradient being created (<see cref="IsNew"/> on Begin) or its handles dragged in <see cref="ImageViewer"/>.
/// <see cref="Component"/> is the updated component; null on End means the drag was cancelled.
/// </summary>
public sealed class ComponentEditEventArgs(MaskComponent? component, EditPhase phase, bool isNew) : EventArgs
{
    public MaskComponent? Component { get; } = component;
    public EditPhase Phase { get; } = phase;
    public bool IsNew { get; } = isNew;
}
