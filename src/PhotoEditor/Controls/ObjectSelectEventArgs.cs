using System;
using PhotoEditor.Core.Masks;

namespace PhotoEditor.Controls;

/// <summary>A click (<see cref="Point"/>) or a dragged box (<see cref="Box"/>) of the Select Object tool, normalised.</summary>
public sealed class ObjectSelectEventArgs(SelectPoint? point, SelectBox? box) : EventArgs
{
    public SelectPoint? Point { get; } = point;
    public SelectBox? Box { get; } = box;
}
