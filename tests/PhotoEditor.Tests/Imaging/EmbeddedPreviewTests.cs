using System.Buffers.Binary;
using System.Diagnostics;
using PhotoEditor.Core.Imaging;
using SkiaSharp;

namespace PhotoEditor.Tests.Imaging;

public sealed class EmbeddedPreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("embedded-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A JPEG whose left half is red and right half blue (so orientation can be checked).</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(0, 0, width / 2f, height, new SKPaint { Color = SKColors.Red });
        }
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A lossless-JPEG header (SOF3), like the RAW sensor data in CR2 / DNG.</summary>
    private static byte[] LosslessJpegHeader() =>
        [0xFF, 0xD8, 0xFF, 0xC3, 0x00, 0x0B, 0x0E, 0x0F, 0xA0, 0x16, 0x00, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9, 0, 0, 0];

    /// <summary>
    /// Little-endian TIFF like a CR2: IFD0 = big JPEG as one strip (compression 6) + orientation,
    /// IFD1 = small JPEG via JPEGInterchangeFormat, IFD0's SubIFD = lossless sensor data (must be ignored).
    /// </summary>
    private static byte[] Tiff(byte[] big, byte[] small, byte[] raw, ushort orientation)
    {
        var file = new List<byte>();
        void U16(int v) => file.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(long v) => file.AddRange(BitConverter.GetBytes((uint)v));
        void Entry(int tag, int type, long value) { U16(tag); U16(type); U32(1); U32(type == 3 ? (ushort)value : value); }

        const int ifd0 = 8, ifd0Entries = 5, ifd1 = ifd0 + 2 + ifd0Entries * 12 + 4, ifd1Entries = 2;
        const int sub = ifd1 + 2 + ifd1Entries * 12 + 4, subEntries = 3;
        int data = sub + 2 + subEntries * 12 + 4;
        int bigAt = data, smallAt = bigAt + big.Length, rawAt = smallAt + small.Length;

        file.AddRange("II"u8.ToArray()); U16(42); U32(ifd0);
        U16(ifd0Entries);
        Entry(259, 3, 6); Entry(273, 4, bigAt); Entry(274, 3, orientation); Entry(279, 4, big.Length); Entry(330, 4, sub);
        U32(ifd1);
        U16(ifd1Entries);
        Entry(513, 4, smallAt); Entry(514, 4, small.Length);
        U32(0);
        U16(subEntries);
        Entry(259, 3, 6); Entry(273, 4, rawAt); Entry(279, 4, raw.Length);
        U32(0);
        file.AddRange(big); file.AddRange(small); file.AddRange(raw);
        return file.ToArray();
    }

    private static byte[] Box(string type, params byte[][] content)
    {
        int size = 8 + content.Sum(c => c.Length);
        var box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        int at = 8;
        foreach (var c in content)
        {
            c.CopyTo(box, at);
            at += c.Length;
        }
        return box;
    }

    private static byte[] Be32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Be64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }
    private static byte[] Uuid(string guid) => new Guid(guid).ToByteArray(bigEndian: true);

    /// <summary>CR3 layout: moov (first trak → full JPEG in mdat, Canon uuid with THMB), PRVW uuid, mdat.</summary>
    private static byte[] Cr3(byte[] full, byte[] preview, byte[] thumb)
    {
        byte[] Moov(long fullOffset) => Box("moov",
            Box("uuid", Uuid("85c0b687-820f-11e0-8111-f4ce462b6a48"),
                Box("THMB", [0, 0, 0, 0, 0, 160, 0, 120], Be32((uint)thumb.Length), [0, 1, 0, 0], thumb)),
            Box("trak", Box("mdia", Box("minf", Box("stbl",
                Box("stsz", Be32(0), Be32((uint)full.Length), Be32(1)),
                Box("co64", Be32(0), Be32(1), Be64((ulong)fullOffset)))))));
        var ftyp = Box("ftyp", "crx "u8.ToArray(), Be32(1), "crx isom"u8.ToArray());
        var prvw = Box("uuid", Uuid("eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16"), new byte[8],
            Box("PRVW", [0, 0, 0, 0, 0, 1, 6, 84, 4, 56, 0, 1], Be32((uint)preview.Length), preview));
        long mdatContent = ftyp.Length + Moov(0).Length + prvw.Length + 8;
        return [.. ftyp, .. Moov(mdatContent), .. prvw, .. Box("mdat", full)];
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Tiff_FindsStripAndThumbnail_IgnoresLosslessSensorData()
    {
        var path = Write("a.cr2", Tiff(Jpeg(300, 200), Jpeg(60, 40), LosslessJpegHeader(), 1));
        using var stream = File.OpenRead(path);
        var found = EmbeddedPreview.Find(stream).OrderBy(j => j.Width).Select(j => (j.Width, j.Height)).ToList();
        Assert.Equal([(60, 40), (300, 200)], found);
    }

    [Fact]
    public void Load_PicksTheSmallestBigEnoughPreview_AndScalesIt()
    {
        var path = Write("a.nef", Tiff(Jpeg(300, 200), Jpeg(60, 40), LosslessJpegHeader(), 1));
        using var small = EmbeddedPreview.Load(path, 50)!;
        Assert.Equal((50, 33), (small.Width, small.Height)); // from the 60 px thumbnail
        using var mid = EmbeddedPreview.Load(path, 120)!;
        Assert.Equal((120, 80), (mid.Width, mid.Height)); // from the 300 px preview
        using var full = EmbeddedPreview.Load(path)!;
        Assert.Equal((300, 200), (full.Width, full.Height));
    }

    [Fact]
    public void Load_TurnsThePreviewUprightWithTheRawOrientation()
    {
        // Orientation 6 (RightTop): the camera was turned clockwise; the upright image is portrait with red on top.
        var path = Write("p.cr2", Tiff(Jpeg(300, 200), Jpeg(60, 40), LosslessJpegHeader(), 6));
        using var upright = EmbeddedPreview.Load(path)!;
        Assert.Equal((200, 300), (upright.Width, upright.Height));
        Assert.True(upright.GetPixel(100, 20).Red > 200);
        Assert.True(upright.GetPixel(100, 280).Blue > 200);
    }

    [Fact]
    public void Cr3_FindsFullSizeTrack_Prvw_AndThmb()
    {
        var path = Write("a.cr3", Cr3(Jpeg(600, 400), Jpeg(162, 108), Jpeg(40, 30)));
        using var stream = File.OpenRead(path);
        var found = EmbeddedPreview.Find(stream).OrderBy(j => j.Width).Select(j => (j.Width, j.Height)).ToList();
        Assert.Equal([(40, 30), (162, 108), (600, 400)], found);
        using var full = EmbeddedPreview.Load(path)!;
        Assert.Equal((600, 400), (full.Width, full.Height));
        Assert.True(full.GetPixel(20, 200).Red > 200 && full.GetPixel(580, 200).Blue > 200);
    }

    [Fact]
    public void UnknownOrBrokenFiles_GiveNothing()
    {
        Assert.Null(EmbeddedPreview.Load(Write("x.cr3", [1, 2, 3])));
        Assert.Null(EmbeddedPreview.Load(Write("y.cr2", "II*\0ÿÿÿ\u007f"u8.ToArray().Concat(new byte[32]).ToArray())));
        var cr3 = Cr3(Jpeg(600, 400), Jpeg(162, 108), Jpeg(40, 30));
        Assert.Null(EmbeddedPreview.Load(Write("t.cr3", cr3[..40])));
        // Cut off in the middle: the small previews that are complete still work.
        using var partial = EmbeddedPreview.Load(Write("h.cr3", cr3[..(cr3.Length / 2)]));
        Assert.True(partial is { Width: <= 162 });
        Assert.Null(EmbeddedPreview.Load(Path.Combine(_dir, "missing.cr3")));
    }

    /// <summary>Real Canon CR2 when PHOTOEDITOR_RAW points to one (otherwise does nothing).</summary>
    [Fact]
    public void RealRaw_HasAFullSizePreview_ReadFast()
    {
        var path = Environment.GetEnvironmentVariable("PHOTOEDITOR_RAW");
        if (path is null || !File.Exists(path))
            return;
        using (EmbeddedPreview.Load(path)) { } // warm up
        var watch = Stopwatch.StartNew();
        using var full = EmbeddedPreview.Load(path);
        var fullTime = watch.Elapsed;
        watch.Restart();
        using var thumb = EmbeddedPreview.Load(path, 320);
        var thumbTime = watch.Elapsed;
        Assert.NotNull(full);
        Assert.NotNull(thumb);
        Assert.True(full.Width >= 2000, $"{full.Width} × {full.Height}");
        Assert.Equal(320, Math.Max(thumb.Width, thumb.Height));
        watch.Restart();
        using var decoded = ImageLoader.Load(path);
        Console.WriteLine($"full RAW decode {decoded.Width} × {decoded.Height}: {watch.Elapsed.TotalMilliseconds:0} ms");
        Console.WriteLine($"embedded preview {full.Width} × {full.Height}: {fullTime.TotalMilliseconds:0} ms; thumbnail {thumbTime.TotalMilliseconds:0} ms");
    }
}
