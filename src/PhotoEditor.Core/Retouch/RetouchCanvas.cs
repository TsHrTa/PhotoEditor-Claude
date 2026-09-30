using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Core.Retouch;

/// <summary>
/// A photo with its spots applied, kept up to date in place: when the spots change, only the areas of the
/// changed spots (and of the spots that touch them) are restored from the photo and redone, so moving a spot on a
/// 20+ MP photo costs milliseconds instead of copying the whole image. Gives the same pixels as
/// <see cref="Retouching.Apply"/>. Not thread-safe: one update at a time.
/// </summary>
public sealed class RetouchCanvas : IDisposable
{
    private readonly SKPixmap _base;
    private readonly SKPixmap? _baseHeadroom;
    private IReadOnlyList<Spot> _applied = [];

    /// <param name="photo">The unretouched photo (RGBA8888; must stay alive and unchanged while the canvas is used).</param>
    public RetouchCanvas(SKImage photo)
    {
        _base = photo.PeekPixels() ?? throw new ArgumentException("The photo must be a raster image.", nameof(photo));
        if (_base.ColorType != SKColorType.Rgba8888)
            throw new ArgumentException("The photo must be RGBA8888.", nameof(photo));
        Bitmap = Copy(_base);
        if (Headroom.Of(photo) is { } headroom && headroom.Width == photo.Width && headroom.Height == photo.Height)
        {
            _baseHeadroom = headroom.Bitmap.PeekPixels();
            HeadroomBitmap = Copy(_baseHeadroom);
            HeadroomScale = headroom.Scale;
        }
    }

    /// <summary>The retouched photo (changed in place by <see cref="Update"/>).</summary>
    public SKBitmap Bitmap { get; }

    /// <summary>The retouched headroom layer, if the photo has one.</summary>
    public SKBitmap? HeadroomBitmap { get; }
    public float HeadroomScale { get; }

    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    /// <summary>Brings the pixels to <paramref name="spots"/>; false if nothing changed.</summary>
    public bool Update(IReadOnlyList<Spot> spots)
    {
        var changed = _applied.Except(spots).Concat(spots.Except(_applied)).ToList();
        if (changed.Count == 0 && _applied.SequenceEqual(spots))
            return false;
        if (changed.Count == 0)
            changed = [.. spots]; // same spots, other order: redo all

        // Spots to redo: the changed ones, every spot that reads an area being redone (its result depends on it),
        // and every spot that writes where a redone spot reads (so that one sees the pixels as they were at its
        // turn); until nothing new is added. Their circles are restored from the photo and they are applied in order.
        int w = Width, h = Height;
        var dirty = changed.Select(s => Retouching.DestinationBounds(s, w, h)).Where(r => !r.IsEmpty).ToList();
        var reads = new List<SKRectI>();
        var redo = new HashSet<Spot>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var spot in spots)
            {
                if (redo.Contains(spot))
                    continue;
                var (destination, source) = Retouching.ReadBounds(spot, w, h);
                var writes = Retouching.DestinationBounds(spot, w, h);
                if (!dirty.Any(d => d.IntersectsWith(destination) || d.IntersectsWith(source))
                    && !reads.Any(r => r.IntersectsWith(writes)))
                    continue;
                redo.Add(spot);
                dirty.Add(writes);
                reads.Add(destination);
                reads.Add(source);
                grew = true;
            }
        }
        foreach (var rect in dirty)
        {
            Restore(Bitmap, _base, rect);
            if (HeadroomBitmap is not null && _baseHeadroom is not null)
                Restore(HeadroomBitmap, _baseHeadroom, rect);
        }
        foreach (var spot in spots.Where(redo.Contains))
        {
            Retouching.ApplySpot(Bitmap, spot);
            if (HeadroomBitmap is not null)
                Retouching.ApplySpot(HeadroomBitmap, spot);
        }
        _applied = [.. spots];
        return true;
    }

    /// <summary>
    /// The current pixels as an image, without copying (with the retouched headroom attached; the brightness /
    /// haze maps of <paramref name="photo"/> are reused). The image keeps the canvas alive; it shows later updates
    /// too, so make a new one after each <see cref="Update"/> (renderers cache by image).
    /// </summary>
    public SKImage Snapshot(SKImage photo)
    {
        var image = Wrap(Bitmap);
        if (HeadroomBitmap is not null)
            Headroom.Attach(image, new Headroom(HeadroomBitmap, HeadroomScale, Wrap(HeadroomBitmap)));
        Adjustments.ToneBaseMap.Share(photo, image);
        Adjustments.HazeMap.Share(photo, image);
        return image;
    }

    private SKImage Wrap(SKBitmap bitmap)
    {
        using var pixmap = bitmap.PeekPixels();
        // The release callback holds on to the canvas until Skia is done with the image.
        return SKImage.FromPixels(pixmap, (_, context) => GC.KeepAlive(context), this)
            ?? throw new InvalidOperationException("Could not wrap the retouched photo.");
    }

    private static SKBitmap Copy(SKPixmap pixmap)
    {
        var bitmap = new SKBitmap(pixmap.Info);
        if (!pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes))
            throw new InvalidOperationException("Could not copy the photo.");
        return bitmap;
    }

    private static unsafe void Restore(SKBitmap target, SKPixmap source, SKRectI rect)
    {
        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, target.Width, target.Height));
        if (rect.IsEmpty)
            return;
        byte* dst = (byte*)target.GetPixels(), src = (byte*)source.GetPixels();
        for (int y = rect.Top; y < rect.Bottom; y++)
            new ReadOnlySpan<byte>(src + (long)y * source.RowBytes + rect.Left * 4, rect.Width * 4)
                .CopyTo(new Span<byte>(dst + (long)y * target.RowBytes + rect.Left * 4, rect.Width * 4));
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        HeadroomBitmap?.Dispose();
        _base.Dispose();
        _baseHeadroom?.Dispose();
    }
}
