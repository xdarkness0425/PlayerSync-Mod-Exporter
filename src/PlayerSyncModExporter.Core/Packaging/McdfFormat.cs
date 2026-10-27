using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Legacy;
using PlayerSyncModExporter.Core.IO;

namespace PlayerSyncModExporter.Core.Packaging;

/// <summary>
/// PlayerSync character data file (MCDF), version 1, as written by PlayerSync's
/// Services/CharaData/CharaDataFileHandler.cs (SaveCharaFileAsync) and
/// Services/CharaData/Models/MareCharaFileHeader.cs (WriteToStream). Layout, inside a legacy LZ4 stream
/// (K4os.Compression.LZ4.Legacy LZ4Stream, HighCompression):
/// <code>
/// 'M' 'C' 'D' 'F'  (4 bytes)
/// version          (1 byte, = 1)
/// jsonLength       (int32 little endian)
/// json             (UTF-8 MareCharaFileData)
/// payloads         (raw bytes of each Files[i], in order, Files[i].Length bytes each)
/// </code>
/// The format only carries the Player object. This is an independent implementation of the documented
/// layout; the format itself comes from the MIT-licensed PlayerSync client (see THIRD_PARTY_NOTICES.md).
/// </summary>
public static class McdfFormat
{
    public const byte Version = 1;

    public sealed class McdfData
    {
        public string Description { get; set; } = string.Empty;
        public string GlamourerData { get; set; } = string.Empty;
        public string CustomizePlusData { get; set; } = string.Empty;
        public string ManipulationData { get; set; } = string.Empty;
        public List<McdfFile> Files { get; set; } = [];
        public List<McdfSwap> FileSwaps { get; set; } = [];
    }

    public sealed class McdfFile
    {
        public List<string> GamePaths { get; set; } = [];
        public int Length { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    public sealed class McdfSwap
    {
        public List<string> GamePaths { get; set; } = [];
        public string FileSwapPath { get; set; } = string.Empty;
    }

    public static async Task WriteAsync(Stream output, McdfData data, Func<McdfFile, Stream, CancellationToken, Task> writePayload,
        CancellationToken token)
    {
        foreach (var f in data.Files)
            if (f.Length < 0)
                throw new InvalidDataException($"MCDF cannot hold {f.Hash}: files over 2 GiB are not supported by the format.");

        await using var lz4 = new LZ4Stream(output, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression);
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var header = new byte[9];
        header[0] = (byte)'M';
        header[1] = (byte)'C';
        header[2] = (byte)'D';
        header[3] = (byte)'F';
        header[4] = Version;
        BitConverter.TryWriteBytes(header.AsSpan(5), json.Length);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(header, 5, 4);
        lz4.Write(header);
        lz4.Write(json);

        foreach (var file in data.Files)
        {
            token.ThrowIfCancellationRequested();
            await writePayload(file, lz4, token).ConfigureAwait(false);
        }

        await lz4.FlushAsync(token).ConfigureAwait(false);
    }

    public static McdfData ReadHeader(Stream decompressed)
    {
        var magic = new byte[5];
        decompressed.ReadExactly(magic);
        if (Encoding.ASCII.GetString(magic, 0, 4) != "MCDF")
            throw new InvalidDataException("Not an MCDF file.");
        if (magic[4] != Version)
            throw new InvalidDataException($"Unsupported MCDF version {magic[4]}.");

        var lengthBytes = new byte[4];
        decompressed.ReadExactly(lengthBytes);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > 256 * 1024 * 1024)
            throw new InvalidDataException("MCDF header length is invalid.");

        var json = new byte[length];
        decompressed.ReadExactly(json);
        return JsonSerializer.Deserialize<McdfData>(json) ?? throw new InvalidDataException("MCDF header is empty.");
    }

    public static Stream OpenDecompressed(string path)
        => new LZ4Stream(File.OpenRead(path), LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);

    /// <summary>Reads the whole file back and checks every payload against its hash and that nothing trails it.</summary>
    public static async Task<(McdfData Data, List<string> Problems)> VerifyAsync(string path, CancellationToken token)
    {
        var problems = new List<string>();
        await using var stream = OpenDecompressed(path);
        var data = ReadHeader(stream);
        foreach (var file in data.Files)
        {
            var actual = await StreamingCopy.HashExactlyAsync(stream, file.Length, token).ConfigureAwait(false);
            if (!string.Equals(actual, file.Hash, StringComparison.OrdinalIgnoreCase))
                problems.Add($"MCDF payload for {file.Hash} hashes to {actual}.");
        }

        if (stream.ReadByte() != -1)
            problems.Add("MCDF contains trailing data after the declared payloads.");
        return (data, problems);
    }
}
