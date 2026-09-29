using PhotoEditor.Core.Masks;

namespace PhotoEditor.Tests.Masks;

/// <summary>Coverage 1 inside a normalised rectangle, 0 outside.</summary>
internal sealed record RectComponent(float X0, float Y0, float X1, float Y1) : MaskComponent
{
    public override void Render(Span<float> coverage, int width, int height)
    {
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
            coverage[y * width + x] = u >= X0 && u < X1 && v >= Y0 && v < Y1 ? 1f : 0f;
        }
    }
}

/// <summary>The same coverage everywhere.</summary>
internal sealed record ValueComponent(float Value) : MaskComponent
{
    public override void Render(Span<float> coverage, int width, int height) => coverage.Fill(Value);
}
