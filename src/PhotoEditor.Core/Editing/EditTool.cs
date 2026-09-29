namespace PhotoEditor.Core.Editing;

/// <summary>What a left-drag on the image does.</summary>
public enum EditTool
{
    /// <summary>Pan (and edit handles of the selected gradient).</summary>
    None,
    Brush,
    LinearGradient,
    RadialGradient,

    /// <summary>Crop rectangle editing: the viewer shows the whole image with the crop frame.</summary>
    Crop,

    /// <summary>AI selection: click on an object (Alt+click to exclude) or drag a box around it.</summary>
    ObjectSelect,
}
