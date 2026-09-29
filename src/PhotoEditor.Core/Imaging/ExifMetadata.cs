using System.Buffers.Binary;
using System.Text;

namespace PhotoEditor.Core.Imaging;

/// <summary>
/// Minimal EXIF handling: extracts the raw TIFF-structured EXIF block from JPEG / PNG / WebP files,
/// edits the orientation tag and embeds the block into JPEG / PNG output.
/// </summary>
public static class ExifMetadata
{
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const ushort OrientationTag = 0x0112;

    /// <summary>
    /// Returns the EXIF TIFF block of the file (without the "Exif\0\0" prefix), or null.
    /// For RAW files a minimal block (camera, date, exposure) is built from LibRaw's metadata.
    /// </summary>
    public static byte[]? Read(string path)
    {
        if (RawImageLoader.IsRaw(path))
            return RawImageLoader.ReadMetadata(path) is { } raw ? RawImageLoader.BuildExif(raw) : null;
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static byte[]? Read(ReadOnlySpan<byte> file)
    {
        try
        {
            if (file.Length > 4 && file[0] == 0xFF && file[1] == 0xD8)
                return ReadJpeg(file);
            if (file.StartsWith(PngSignature))
                return ReadPng(file);
            if (file.Length > 12 && file[..4].SequenceEqual("RIFF"u8) && file.Slice(8, 4).SequenceEqual("WEBP"u8))
                return ReadWebP(file);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Truncated / corrupt metadata: treat as absent.
        }
        return null;
    }

    private static byte[]? ReadJpeg(ReadOnlySpan<byte> jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9) // start of scan / end of image
                break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2));
            var data = jpeg.Slice(pos + 4, length - 2);
            if (marker == 0xE1 && data.StartsWith(ExifHeader))
                return data[ExifHeader.Length..].ToArray();
            pos += 2 + length;
        }
        return null;
    }

    private static byte[]? ReadPng(ReadOnlySpan<byte> png)
    {
        int pos = PngSignature.Length;
        while (pos + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(pos));
            var type = png.Slice(pos + 4, 4);
            if (type.SequenceEqual("eXIf"u8))
                return StripExifHeader(png.Slice(pos + 8, length));
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                break;
            pos += 12 + length;
        }
        return null;
    }

    private static byte[]? ReadWebP(ReadOnlySpan<byte> webp)
    {
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            var type = webp.Slice(pos, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(webp.Slice(pos + 4));
            if (type.SequenceEqual("EXIF"u8))
                return StripExifHeader(webp.Slice(pos + 8, length));
            pos += 8 + length + (length & 1);
        }
        return null;
    }

    private static byte[] StripExifHeader(ReadOnlySpan<byte> data) =>
        (data.StartsWith(ExifHeader) ? data[ExifHeader.Length..] : data).ToArray();

    /// <summary>Returns the orientation value (1..8) stored in IFD0, or null.</summary>
    public static int? GetOrientation(byte[] tiff) =>
        FindOrientationEntry(tiff, out int valuePos, out bool littleEndian)
            ? (littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(valuePos))
                : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(valuePos)))
            : null;

    /// <summary>Returns a copy with the orientation tag set to 1 (upright), since exported pixels are already rotated.</summary>
    public static byte[] WithNormalOrientation(byte[] tiff)
    {
        var copy = (byte[])tiff.Clone();
        if (FindOrientationEntry(copy, out int valuePos, out bool littleEndian))
        {
            if (littleEndian)
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(valuePos), 1);
            else
                BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(valuePos), 1);
        }
        return copy;
    }

    private static bool FindOrientationEntry(byte[] tiff, out int valuePos, out bool littleEndian)
    {
        valuePos = 0;
        littleEndian = tiff.Length >= 2 && tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (tiff.Length < 8 || !(littleEndian || (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')))
            return false;
        bool le = littleEndian;
        ushort U16(int p) => le ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(p));
        uint U32(int p) => le ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(p));

        long ifd = U32(4);
        if (ifd + 2 > tiff.Length)
            return false;
        int count = U16((int)ifd);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length)
                return false;
            if (U16(entry) == OrientationTag && U16(entry + 2) == 3) // SHORT
            {
                valuePos = entry + 8;
                return true;
            }
        }
        return false;
    }

    /// <summary>Inserts an EXIF APP1 segment into a JPEG file (after SOI / JFIF APP0). Too-large blocks are skipped.</summary>
    public static byte[] EmbedInJpeg(byte[] jpeg, byte[] tiff)
    {
        int segmentLength = 2 + ExifHeader.Length + tiff.Length;
        if (segmentLength > ushort.MaxValue || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return jpeg;

        int insertAt = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0) // keep JFIF APP0 first
            insertAt = 4 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(4));

        using var ms = new MemoryStream(jpeg.Length + segmentLength + 2);
        ms.Write(jpeg, 0, insertAt);
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE1);
        ms.WriteByte((byte)(segmentLength >> 8));
        ms.WriteByte((byte)segmentLength);
        ms.Write(ExifHeader);
        ms.Write(tiff);
        ms.Write(jpeg, insertAt, jpeg.Length - insertAt);
        return ms.ToArray();
    }

    /// <summary>Inserts an eXIf chunk into a PNG file right after IHDR.</summary>
    public static byte[] EmbedInPng(byte[] png, byte[] tiff)
    {
        if (!png.AsSpan().StartsWith(PngSignature))
            return png;
        int ihdrLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8));
        int insertAt = 8 + 12 + ihdrLength;

        var chunk = new byte[12 + tiff.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, tiff.Length);
        Encoding.ASCII.GetBytes("eXIf", 0, 4, chunk, 4);
        tiff.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + tiff.Length), Crc32(chunk.AsSpan(4, 4 + tiff.Length)));

        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result, insertAt);
        png.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        return result;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 as used by PNG chunks.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
