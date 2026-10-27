using System.Globalization;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Model;

namespace PlayerSyncModExporter.Core.Locating;

/// <summary>A local file that may contain the content for a hash. It still has to be verified.</summary>
public sealed record LocatedCandidate(string Path, AssetProvenance Provenance);

/// <summary>Finds local candidate files for a content hash. Implementations must never modify anything.</summary>
public interface IAssetLocator
{
    string Name { get; }

    IEnumerable<LocatedCandidate> Locate(string hash, string extension);
}

/// <summary>
/// Probes PlayerSync's cache folder directly using its naming scheme:
/// <c>FileCacheManager.GetCacheFilePath</c> writes "&lt;cache&gt;/&lt;hash lower&gt;.&lt;ext&gt;",
/// <c>MigrateFileHashToExtension</c> produced upper-cased names, and very old caches had no extension.
/// </summary>
public sealed class CacheFolderLocator : IAssetLocator
{
    private readonly string _cacheFolder;

    public CacheFolderLocator(string cacheFolder) => _cacheFolder = cacheFolder;

    public string Name => "PlayerSync cache folder probe";

    public IEnumerable<LocatedCandidate> Locate(string hash, string extension)
    {
        if (string.IsNullOrWhiteSpace(_cacheFolder) || !Directory.Exists(_cacheFolder))
            yield break;

        var lower = hash.ToLowerInvariant();
        var upper = hash.ToUpperInvariant();
        var names = new List<string>();
        if (!string.IsNullOrEmpty(extension))
        {
            names.Add($"{lower}.{extension}");
            names.Add($"{upper}.{extension}");
            names.Add($"{upper}.{extension.ToUpperInvariant()}");
        }

        names.Add(lower);
        names.Add(upper);

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var path = Path.Combine(_cacheFolder, name);
            if (File.Exists(path))
                yield return new LocatedCandidate(path, AssetProvenance.PlayerSyncCache);
        }
    }
}

/// <summary>
/// Reads only requested content-addressed filenames from a user-chosen resource copy.
/// Does not enumerate, mutate, or import the source directory.
/// </summary>
public sealed class ReadOnlyResourceFolderLocator : IAssetLocator
{
    private readonly CacheFolderLocator _inner;

    public ReadOnlyResourceFolderLocator(string folder) => _inner = new CacheFolderLocator(folder);

    public string Name => "Additional read-only resource folder";

    public IEnumerable<LocatedCandidate> Locate(string hash, string extension)
    {
        if (!Hashing.ContentHash.IsValid(hash) || extension.Length is < 1 or > 12
            || !extension.All(char.IsAsciiLetterOrDigit))
            yield break;

        foreach (var candidate in _inner.Locate(hash, extension))
            yield return candidate with { Provenance = AssetProvenance.AdditionalResourceDirectory };
    }
}

/// <summary>One parsed row of PlayerSync's FileCache.csv.</summary>
public sealed record FileCacheRecord(string Hash, string PrefixedPath, string LastModifiedTicks, long Size, long CompressedSize);

/// <summary>
/// Read-only parser for PlayerSync's <c>FileCache.csv</c> (PlayerSync/FileCache/FileCacheManager.cs,
/// <c>WriteOutFullCsvNow</c> / <c>StartAsync</c>). Each line is
/// <c>hash|prefixedPath|lastWriteTicks|size|compressedSize</c>, where the path starts with
/// <c>{cache}\</c> (PlayerSync cache folder) or <c>{penumbra}\</c> (Penumbra mod directory).
/// The file is opened with full sharing and is never written.
/// </summary>
public static class FileCacheCsv
{
    public const string CachePrefix = "{cache}";
    public const string PenumbraPrefix = "{penumbra}";

    public static IReadOnlyList<FileCacheRecord> Read(string csvPath, ICollection<string>? warnings = null)
    {
        var result = new List<FileCacheRecord>();
        if (!File.Exists(csvPath))
            return result;

        using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (TryParseLine(line, out var record))
                result.Add(record);
            else
                warnings?.Add($"FileCache.csv line {lineNo} is malformed and was ignored.");
        }

        return result;
    }

    public static bool TryParseLine(string line, out FileCacheRecord record)
    {
        record = null!;
        var parts = line.Split('|');
        if (parts.Length < 3)
            return false;

        var hash = parts[0].Trim();
        if (!Hashing.ContentHash.IsValid(hash))
            return false;

        var path = parts[1].Trim();
        if (!path.StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(PenumbraPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        long size = -1, compressed = -1;
        if (parts.Length > 3)
            _ = long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
        if (parts.Length > 4)
            _ = long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out compressed);

        record = new FileCacheRecord(hash.ToUpperInvariant(), path, parts[2].Trim(), size, compressed);
        return true;
    }

    /// <summary>Resolves a prefixed path against the configured roots, refusing anything that escapes them.</summary>
    public static LocatedCandidate? Resolve(FileCacheRecord record, string? cacheFolder, string? penumbraModDirectory)
    {
        string? root;
        string rest;
        AssetProvenance provenance;
        if (record.PrefixedPath.StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = cacheFolder;
            rest = record.PrefixedPath[CachePrefix.Length..];
            provenance = AssetProvenance.PlayerSyncCache;
        }
        else
        {
            root = penumbraModDirectory;
            rest = record.PrefixedPath[PenumbraPrefix.Length..];
            provenance = AssetProvenance.PenumbraModDirectory;
        }

        if (string.IsNullOrWhiteSpace(root))
            return null;

        var relative = rest.TrimStart('\\', '/').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return PathSafety.TryCombineUnderRoot(root, relative, out var full) ? new LocatedCandidate(full, provenance) : null;
    }
}

/// <summary>Locator backed by a parsed FileCache.csv snapshot.</summary>
public sealed class FileCacheCsvLocator : IAssetLocator
{
    private readonly ILookup<string, FileCacheRecord> _byHash;
    private readonly string? _cacheFolder;
    private readonly string? _penumbraDirectory;

    public FileCacheCsvLocator(IEnumerable<FileCacheRecord> records, string? cacheFolder, string? penumbraDirectory)
    {
        _byHash = records.ToLookup(r => r.Hash, StringComparer.OrdinalIgnoreCase);
        _cacheFolder = cacheFolder;
        _penumbraDirectory = penumbraDirectory;
    }

    public string Name => "FileCache.csv index";

    public IEnumerable<LocatedCandidate> Locate(string hash, string extension)
    {
        foreach (var record in _byHash[hash])
            if (FileCacheCsv.Resolve(record, _cacheFolder, _penumbraDirectory) is { } candidate)
                yield return candidate;
    }
}

/// <summary>Locator backed by a callback (used by the plugin for PlayerSync's live in-memory index).</summary>
public sealed class DelegateLocator : IAssetLocator
{
    private readonly Func<string, string, IEnumerable<LocatedCandidate>> _locate;

    public DelegateLocator(string name, Func<string, string, IEnumerable<LocatedCandidate>> locate)
    {
        Name = name;
        _locate = locate;
    }

    public string Name { get; }

    public IEnumerable<LocatedCandidate> Locate(string hash, string extension) => _locate(hash, extension);
}
