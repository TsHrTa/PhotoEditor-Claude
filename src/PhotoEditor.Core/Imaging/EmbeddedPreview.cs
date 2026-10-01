using System.Buffers.Binary;
using SkiaSharp;

namespace PhotoEditor.Core.Imaging;

/// <summary>A baseline / progressive JPEG stored inside another file.</summary>
public readonly record struct EmbeddedJpeg(long Offset, long Length, int Width, int Height)
{
    public int LongSide => Math.Max(Width, Height);
}

/// <summary>
/// Reads the JPEG previews cameras store inside RAW files, so a RAW can be shown in a fraction of a second
/// without demosaicing it. Supported: CR3 (full-size JPEG track, PRVW and THMB boxes), TIFF-based RAWs such as
/// CR2, NEF, ARW, DNG, ORF, RW2, PEF (JPEGs referenced from IFD0, IFD1… and SubIFDs) and RAF. Previews are
/// stored in sensor orientation; <see cref="Load"/> turns them upright with the RAW's orientation tag.
/// </summary>
public static class EmbeddedPreview
{
    private const int MaxIfds = 64;

    /// <summary>
    /// Decodes the smallest embedded preview whose long side is at least <paramref name="longSide"/> (or the
    /// largest one), scaled down to at most <paramref name="longSide"/> and upright; null if there is none.
    /// </summary>
    public static SKBitmap? Load(string path, int longSide = int.MaxValue)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var jpegs = Find(stream);
            if (jpegs.Count == 0)
                return null;
            var pick = jpegs.OrderBy(j => j.LongSide).FirstOrDefault(j => j.LongSide >= longSide);
            if (pick.Length == 0)
                pick = jpegs.MaxBy(j => j.LongSide);
            var bytes = new byte[pick.Length];
            stream.Position = pick.Offset;
            stream.ReadExactly(bytes);
            var decoded = DecodeScaled(bytes, longSide);
            if (decoded is null)
                return null;
            var fitted = FitLongSide(decoded, longSide);
            if (!ReferenceEquals(fitted, decoded))
                decoded.Dispose();
            var upright = ImageLoader.ApplyOrientation(fitted, RawImageLoader.ReadOrientation(path));
            if (!ReferenceEquals(upright, fitted))
                fitted.Dispose();
            return upright;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>All decodable JPEGs embedded in <paramref name="stream"/> (empty when none or unknown format).</summary>
    public static IReadOnlyList<EmbeddedJpeg> Find(Stream stream)
    {
        var head = new byte[16];
        stream.Position = 0;
        if (stream.Read(head) < 16)
            return [];
        var candidates = new List<(long Offset, long Length)>();
        if (head.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            FindInBoxes(stream, candidates);
        else if (head.AsSpan(0, 15).SequenceEqual("FUJIFILMCCD-RAW"u8))
            FindInRaf(stream, candidates);
        else if (head[0] == head[1] && head[0] is (byte)'I' or (byte)'M')
            FindInTiff(stream, 0, candidates);

        var result = new List<EmbeddedJpeg>();
        foreach (var (offset, length) in candidates.Distinct())
        {
            if (offset <= 0 || length < 4 || offset + length > stream.Length)
                continue;
            if (ReadJpegSize(stream, offset, length) is { } size)
                result.Add(new EmbeddedJpeg(offset, length, size.Width, size.Height));
        }
        return result;
    }

    // ---- TIFF (CR2, NEF, ARW, DNG, ORF, RW2, PEF, …) ----

    private static void FindInTiff(Stream stream, long tiffStart, List<(long, long)> found)
    {
        var header = ReadAt(stream, tiffStart, 8);
        if (header.Length < 8)
            return;
        bool little = header[0] == 'I';
        uint first = U32(header, 4, little);
        var pending = new Queue<long>();
        var seen = new HashSet<long>();
        pending.Enqueue(first);
        while (pending.Count > 0 && seen.Count < MaxIfds)
        {
            long ifd = pending.Dequeue();
            if (ifd <= 0 || !seen.Add(ifd))
                continue;
            long at = tiffStart + ifd;
            var countBytes = ReadAt(stream, at, 2);
            if (countBytes.Length < 2)
                continue;
            int count = U16(countBytes, 0, little);
            if (count is 0 or > 1000)
                continue;
            var entries = ReadAt(stream, at + 2, count * 12 + 4);
            if (entries.Length < count * 12 + 4)
                continue;

            long jpegOffset = 0, jpegLength = 0, stripOffset = 0, stripLength = 0;
            int compression = 0;
            for (int i = 0; i < count; i++)
            {
                int e = i * 12;
                int tag = U16(entries, e, little), type = U16(entries, e + 2, little);
                uint n = U32(entries, e + 4, little);
                uint value = type == 3 ? U16(entries, e + 8, little) : U32(entries, e + 8, little);
                switch (tag)
                {
                    case 259: compression = (int)value; break;
                    case 273 when n == 1: stripOffset = value; break;
                    case 279 when n == 1: stripLength = value; break;
                    case 513: jpegOffset = value; break;
                    case 514: jpegLength = value; break;
                    case 330: // SubIFDs
                        if (n == 1)
                            pending.Enqueue(value);
                        else if (n <= 16)
                        {
                            var offsets = ReadAt(stream, tiffStart + value, (int)n * 4);
                            for (int k = 0; k + 4 <= offsets.Length; k += 4)
                                pending.Enqueue(U32(offsets, k, little));
                        }
                        break;
                }
            }
            if (jpegOffset > 0 && jpegLength > 0)
                found.Add((tiffStart + jpegOffset, jpegLength));
            // CR2 IFD0 / DNG previews: one JPEG strip (compression 6 = old-style JPEG, 7 = JPEG).
            if (compression is 6 or 7 && stripOffset > 0 && stripLength > 0)
                found.Add((tiffStart + stripOffset, stripLength));
            pending.Enqueue(U32(entries, count * 12, little)); // next IFD
        }
    }

    // ---- RAF (Fujifilm) ----

    private static void FindInRaf(Stream stream, List<(long, long)> found)
    {
        var header = ReadAt(stream, 84, 8);
        if (header.Length == 8)
            found.Add((BinaryPrimitives.ReadUInt32BigEndian(header), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4))));
    }

    // ---- CR3 (ISO base media file) ----

    private static readonly Guid PreviewUuid = new("eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16");
    private static readonly Guid CanonUuid = new("85c0b687-820f-11e0-8111-f4ce462b6a48");

    private static void FindInBoxes(Stream stream, List<(long, long)> found)
    {
        bool firstTrack = true;
        foreach (var box in Boxes(stream, 0, stream.Length))
        {
            if (box.Type == "moov")
            {
                foreach (var child in Boxes(stream, box.ContentStart, box.End))
                {
                    if (child.Type == "trak" && firstTrack)
                    {
                        // Canon's first track holds the full-size JPEG (one sample).
                        firstTrack = false;
                        if (FirstSample(stream, child) is { } sample)
                            found.Add(sample);
                    }
                    else if (child.Type == "uuid" && ReadUuid(stream, child) == CanonUuid)
                    {
                        foreach (var inner in Boxes(stream, child.ContentStart + 16, child.End))
                            if (inner.Type == "THMB")
                                AddJpegInBox(stream, inner, found);
                    }
                }
            }
            else if (box.Type == "uuid" && ReadUuid(stream, box) == PreviewUuid)
            {
                // 16-byte UUID, 8 unknown bytes, then the "PRVW" box with a 1620-px JPEG.
                foreach (var inner in Boxes(stream, box.ContentStart + 24, box.End))
                    if (inner.Type == "PRVW")
                        AddJpegInBox(stream, inner, found);
            }
        }
    }

    private readonly record struct Box(string Type, long Start, long ContentStart, long End);

    private static IEnumerable<Box> Boxes(Stream stream, long start, long end)
    {
        long at = start;
        for (int guard = 0; at + 8 <= end && guard < 10_000; guard++)
        {
            var header = ReadAt(stream, at, 16);
            if (header.Length < 8)
                yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            long content = at + 8;
            if (size == 1 && header.Length == 16)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                content = at + 16;
            }
            else if (size == 0)
                size = end - at;
            if (size < content - at || at + size > end)
                yield break;
            yield return new Box(type, at, content, at + size);
            at += size;
        }
    }

    private static Guid? ReadUuid(Stream stream, Box box)
    {
        var bytes = ReadAt(stream, box.ContentStart, 16);
        return bytes.Length == 16 ? new Guid(bytes, bigEndian: true) : null;
    }

    /// <summary>A JPEG starts somewhere in the box's small header (sizes, dimensions) and runs to the box end.</summary>
    private static void AddJpegInBox(Stream stream, Box box, List<(long, long)> found)
    {
        var head = ReadAt(stream, box.ContentStart, 64);
        int soi = head.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
        if (soi >= 0)
            found.Add((box.ContentStart + soi, box.End - box.ContentStart - soi));
    }

    /// <summary>Offset and size of the first sample of a track (from its stsz and co64 / stco boxes).</summary>
    private static (long, long)? FirstSample(Stream stream, Box trak)
    {
        var stbl = Find(stream, trak, "mdia", "minf", "stbl");
        if (stbl is null)
            return null;
        long offset = 0, size = 0;
        foreach (var box in Boxes(stream, stbl.Value.ContentStart, stbl.Value.End))
        {
            var content = ReadAt(stream, box.ContentStart, 20);
            if (box.Type == "stsz" && content.Length >= 12)
            {
                size = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(4));
                if (size == 0 && content.Length >= 16)
                    size = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(12));
            }
            else if (box.Type == "co64" && content.Length >= 16)
                offset = (long)BinaryPrimitives.ReadUInt64BigEndian(content.AsSpan(8));
            else if (box.Type == "stco" && content.Length >= 12)
                offset = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(8));
        }
        return offset > 0 && size > 0 ? (offset, size) : null;
    }

    private static Box? Find(Stream stream, Box parent, params string[] path)
    {
        var current = parent;
        foreach (var type in path)
        {
            Box? next = null;
            foreach (var child in Boxes(stream, current.ContentStart, current.End))
            {
                if (child.Type == type)
                {
                    next = child;
                    break;
                }
            }
            if (next is null)
                return null;
            current = next.Value;
        }
        return current;
    }

    // ---- JPEG ----

    /// <summary>
    /// Width and height from the JPEG's frame header, or null if it isn't a JPEG that SkiaSharp can decode
    /// (lossless JPEG, used for RAW sensor data in CR2 / DNG, is rejected).
    /// </summary>
    public static (int Width, int Height)? ReadJpegSize(Stream stream, long offset, long length)
    {
        var data = ReadAt(stream, offset, (int)Math.Min(length, 256 * 1024));
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;
        int at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF)
                return null;
            byte marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (at + 9 > data.Length)
                    return null;
                int height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 5));
                int width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 7));
                return width > 0 && height > 0 ? (width, height) : null;
            }
            if (marker is >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return null; // lossless / arithmetic / hierarchical: not a preview
            if (marker is 0xD9 or 0xDA)
                return null; // image data before a frame header
            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
        }
        return null;
    }

    /// <summary>Decodes a JPEG, using the decoder's fast 1/2 – 1/8 downscaling when it stays ≥ <paramref name="longSide"/>.</summary>
    public static SKBitmap? DecodeScaled(byte[] bytes, int longSide)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;
        int full = Math.Max(codec.Info.Width, codec.Info.Height);
        var size = codec.Info.Size;
        foreach (float scale in new[] { 0.125f, 0.25f, 0.5f })
        {
            var scaled = codec.GetScaledDimensions(scale);
            if (full * scale >= longSide && scaled.Width > 0)
            {
                size = scaled;
                break;
            }
        }
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// <summary>Downscales to at most <paramref name="longSide"/> on the long side (returns the input if it already fits).</summary>
    public static SKBitmap FitLongSide(SKBitmap source, int longSide)
    {
        var (w, h) = PreviewImage.PreviewSize(source.Width, source.Height, longSide);
        if (w == source.Width && h == source.Height)
            return source;
        if (LinearResampler.TryResize(source, null, w, h) is { } resized)
            return resized.Photo;
        return source.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul),
                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
               ?? throw new InvalidOperationException("Could not resize the preview.");
    }

    // ---- helpers ----

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || offset >= stream.Length || count <= 0)
            return [];
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ushort U16(byte[] b, int at, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at))
        : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private static uint U32(byte[] b, int at, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at))
        : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
}
