using System.Buffers;
using System.Security.Cryptography;
using PlayerSyncModExporter.Core.Hashing;

namespace PlayerSyncModExporter.Core.IO;

public sealed class SourceChangedException : IOException
{
    public SourceChangedException(string path, string expected, string actual)
        : base($"'{path}' changed after analysis (expected {expected}, read {actual}). Re-run the analysis and export again.")
    {
        Path = path;
    }

    public string Path { get; }
}

public static class StreamingCopy
{
    public const int BufferSize = 1 << 20;

    /// <summary>
    /// Copies a source file into <paramref name="destination"/> with a bounded buffer while computing its SHA-1,
    /// and fails if the bytes no longer match <paramref name="expectedHash"/> (e.g. PlayerSync replaced the cache file).
    /// </summary>
    public static async Task<long> CopyVerifiedAsync(string sourcePath, Stream destination, string expectedHash,
        IProgress<long>? bytesProgress, CancellationToken token)
    {
        await using var source = ContentHash.OpenSourceForRead(sourcePath);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
                bytesProgress?.Report(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new SourceChangedException(sourcePath, expectedHash, actual);
        return total;
    }

    /// <summary>Hashes exactly <paramref name="length"/> bytes from <paramref name="stream"/> (used for MCDF verification).</summary>
    public static async Task<string> HashExactlyAsync(Stream stream, long length, CancellationToken token)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, remaining)), token).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Unexpected end of data.");
                sha.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static bool IsDiskFull(IOException e)
    {
        // ERROR_HANDLE_DISK_FULL (0x27), ERROR_DISK_FULL (0x70) on Windows; ENOSPC (28) elsewhere.
        var code = e.HResult & 0xFFFF;
        return code is 0x27 or 0x70 || (!OperatingSystem.IsWindows() && code == 28)
               || e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);
    }
}
