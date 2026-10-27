using System.Buffers.Binary;

namespace PlayerSyncModExporter.Core.Textures;

/// <summary>What the header of an FFXIV .tex file says, and whether it is consistent with the file.</summary>
public sealed record TexCheck(bool Valid, string? Format, int Width, int Height, int Depth, int MipLevels, int ArraySize, string? Problem);

/// <summary>
/// Checks the 80-byte FFXIV texture header (layout as Lumina's TexFile.TexHeader: attribute, format, width, height, depth,
/// mip count, array size, 3 LOD offsets, 13 surface offsets) against the file length. It decodes no pixel data and never
/// changes bytes; it only establishes that a file is a structurally plausible texture.
/// </summary>
public static class TexFileValidator
{
    public const int HeaderSize = 80;
    public const int MaxDimension = 16384;
    public const int MaxMipLevels = 13;

    // Lumina TexFile.TextureFormat values. Bits 4-7 encode log2(bits per pixel); bits 12-15 the type (3 and 6 are block compressed).
    private static readonly Dictionary<uint, string> Formats = new()
    {
        [0x1130] = "L8", [0x1131] = "A8", [0x1440] = "B4G4R4A4", [0x1441] = "B5G5R5A1", [0x1450] = "B8G8R8A8", [0x1451] = "B8G8R8X8",
        [0x2150] = "R32F", [0x2250] = "R16G16F", [0x2260] = "R32G32F", [0x2460] = "R16G16B16A16F", [0x2470] = "R32G32B32A32F",
        [0x3420] = "BC1", [0x3430] = "BC2", [0x3431] = "BC3", [0x4140] = "D16", [0x4250] = "D24S8",
        [0x6120] = "BC4", [0x6230] = "BC5", [0x6330] = "BC6H", [0x6432] = "BC7",
    };

    public static async Task<TexCheck> CheckFileAsync(string path, CancellationToken token = default)
    {
        await using var stream = Hashing.ContentHash.OpenSourceForRead(path);
        var header = new byte[HeaderSize];
        var read = 0;
        while (read < HeaderSize)
        {
            var n = await stream.ReadAsync(header.AsMemory(read), token).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return Check(header.AsSpan(0, read), stream.Length);
    }

    public static TexCheck Check(ReadOnlySpan<byte> header, long fileLength)
    {
        if (header.Length < HeaderSize || fileLength < HeaderSize)
            return Invalid(null, 0, 0, 0, 0, 0, $"The file is {fileLength} bytes, shorter than the {HeaderSize}-byte texture header.");

        var formatCode = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        var mips = header[14] & 0x7F;
        int arraySize = header[15];
        var format = Formats.GetValueOrDefault(formatCode);

        if (format == null)
            return Invalid($"0x{formatCode:X4}", width, height, depth, mips, arraySize, $"Unknown texture format 0x{formatCode:X4}.");
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension || depth > 4096)
            return Invalid(format, width, height, depth, mips, arraySize, $"Implausible dimensions {width}x{height}x{depth}.");
        if (mips is 0 or > MaxMipLevels)
            return Invalid(format, width, height, depth, mips, arraySize, $"Implausible mip level count {mips}.");

        var previous = 0u;
        for (var i = 0; i < mips; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[(28 + 4 * i)..]);
            if (i == 0 && offset < HeaderSize)
                return Invalid(format, width, height, depth, mips, arraySize, $"The first surface starts at {offset}, inside the header.");
            if (offset == 0)
                continue;
            if (offset < previous || offset >= fileLength)
                return Invalid(format, width, height, depth, mips, arraySize, $"Surface offset {offset} for mip {i} is out of order or past the end of the file.");
            previous = offset;
        }

        var first = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        var needed = SurfaceBytes(formatCode, width, height);
        if (first + needed > fileLength)
            return Invalid(format, width, height, depth, mips, arraySize,
                $"A {width}x{height} {format} surface needs {needed} bytes, but only {fileLength - first} follow the header.");

        return new TexCheck(true, format, width, height, Math.Max(depth, 1), mips, Math.Max(arraySize, 1), null);
    }

    /// <summary>Bytes of one 2D surface of the largest mip level.</summary>
    public static long SurfaceBytes(uint formatCode, int width, int height)
    {
        var bitsPerPixel = 1L << (int)((formatCode & 0xF0) >> 4);
        var type = (formatCode >> 12) & 0xF;
        if (type is 3 or 6)
            return Math.Max(1L, (width + 3) / 4) * Math.Max(1L, (height + 3) / 4) * (16 * bitsPerPixel / 8);
        return (long)width * height * bitsPerPixel / 8;
    }

    private static TexCheck Invalid(string? format, int w, int h, int d, int mips, int array, string problem)
        => new(false, format, w, h, d, mips, array, problem);
}
