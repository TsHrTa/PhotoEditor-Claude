namespace PhotoEditor.Core.Editing;

/// <summary>What a left-drag on the image does.</summary>
public enum EditTool
{
    /// <summary>Pan (and edit handles of the selected gradient).</summary>
    None,
    Brush,
    LinearGradient,
    RadialGradient,
}
