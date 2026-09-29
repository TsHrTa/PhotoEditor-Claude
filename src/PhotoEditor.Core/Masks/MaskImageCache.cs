using SkiaSharp;

namespace PhotoEditor.Core.Masks;

/// <summary>
/// Caches rasterised mask images for the live preview. Masks are immutable, so an entry stays
/// valid as long as the mask's component list (same instance) and the size are unchanged.
/// </summary>
public sealed class MaskImageCache
{
    private readonly Dictionary<Guid, Entry> _entries = [];

    private sealed record Entry(object Components, int Width, int Height, SKImage Image);

    /// <summary>Returns the mask as a Gray8 image of the given size, rasterising only when it changed.</summary>
    public SKImage Get(Mask mask, int width, int height)
    {
        if (_entries.TryGetValue(mask.Id, out var e) && ReferenceEquals(e.Components, mask.Components)
            && e.Width == width && e.Height == height)
            return e.Image;

        using var bitmap = MaskRasterizer.RasterizeToBitmap(mask, width, height);
        var image = SKImage.FromBitmap(bitmap);
        // Old images may still be referenced by the render thread; the GC releases them.
        _entries[mask.Id] = new Entry(mask.Components, width, height, image);
        return image;
    }

    /// <summary>Forgets masks that no longer exist.</summary>
    public void Retain(IEnumerable<Mask> masks)
    {
        var live = masks.Select(m => m.Id).ToHashSet();
        foreach (var id in _entries.Keys.Where(id => !live.Contains(id)).ToList())
            _entries.Remove(id);
    }
}
