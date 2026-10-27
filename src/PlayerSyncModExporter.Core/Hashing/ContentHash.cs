using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PlayerSyncModExporter.Core.Hashing;

/// <summary>
/// PlayerSync identifies file content by the upper-case hex SHA-1 of the raw (decompressed) bytes
/// (PlayerSync/Utils/Crypto.cs, Crypto.GetFileHash). This class reproduces that scheme independently.
/// </summary>
public static class ContentHash
{
    public const int HexLength = 40;

    public static bool IsValid(string? hash)
        => hash is { Length: HexLength } && hash.All(Uri.IsHexDigit);

    public static string Normalize(string hash) => hash.Trim().ToUpperInvariant();

    public static async Task<string> ComputeAsync(Stream stream, CancellationToken token = default)
        => Convert.ToHexString(await SHA1.HashDataAsync(stream, token).ConfigureAwait(false));

    public static async Task<string> ComputeFileAsync(string path, CancellationToken token = default)
    {
        await using var fs = OpenSourceForRead(path);
        return await ComputeAsync(fs, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a source file strictly read-only and allows other processes (PlayerSync, Penumbra, the game)
    /// to keep reading, writing or deleting it, so the exporter never blocks or locks the cache.
    /// </summary>
    public static FileStream OpenSourceForRead(string path)
        => new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 1 << 16,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
}

/// <summary>
/// Memoizes file hashes by (path, length, last write time) so repeated analyses do not rehash
/// unchanged multi-gigabyte caches. Any change to size or timestamp invalidates the entry.
/// </summary>
public sealed class FileHashCache
{
    private readonly record struct Key(string Path, long Length, long LastWriteTicks);

    private readonly ConcurrentDictionary<Key, string> _hashes = new();

    public int Count => _hashes.Count;

    public async Task<string> GetOrComputeAsync(FileInfo file, CancellationToken token = default)
    {
        file.Refresh();
        var key = new Key(Path.GetFullPath(file.FullName), file.Length, file.LastWriteTimeUtc.Ticks);
        if (_hashes.TryGetValue(key, out var cached))
            return cached;

        var hash = await ContentHash.ComputeFileAsync(file.FullName, token).ConfigureAwait(false);
        _hashes[key] = hash;
        return hash;
    }

    public void Clear() => _hashes.Clear();
}
