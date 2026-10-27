using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Model;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlayerSyncModExporter.Core.SourceQuality;

/// <summary>
/// Mirrors PlayerSync's <c>MareSynchronos.MareConfiguration.Configurations.CompressedAlternateUsage</c>
/// (PlayerPerformanceConfig.cs). Numeric values must stay identical because the adapter converts by value.
/// </summary>
public enum TextureSourceMode
{
    /// <summary>"Always use source quality": the exact textures the other player uploaded.</summary>
    AlwaysSourceQuality = 0,

    /// <summary>"Use automatically compressed textures for new downloads".</summary>
    CompressedNewDownloads = 1,

    /// <summary>"Always use automatically compressed textures" (PlayerSync's default).</summary>
    AlwaysCompressed = 3,
}

/// <summary>PlayerSync's texture compression setting as it applies to one pair at capture time. Holds no account identifiers.</summary>
public sealed record TextureSourceSetting
{
    /// <summary>PlayerPerformanceConfig.DefaultTextureCompressionMode, used when the setting was never changed.</summary>
    public const TextureSourceMode PlayerSyncDefault = TextureSourceMode.AlwaysCompressed;

    public static readonly TextureSourceSetting Unknown = new();

    /// <summary>The global mode, or null when PlayerSync's setting could not be read.</summary>
    public TextureSourceMode? GlobalMode { get; init; }

    /// <summary>The pair is on PlayerSync's "Override UIDs" list (always source quality), set by the user.</summary>
    public bool PairOverride { get; init; }

    /// <summary>The mode PlayerSync's PairHandler.ComputeCompressedAlternateUsage returns for this pair, or null if unknown.</summary>
    public TextureSourceMode? Effective => PairOverride ? TextureSourceMode.AlwaysSourceQuality : GlobalMode;

    /// <summary>Converts PlayerSync's raw values. A null <paramref name="rawMode"/> means "never set", which is PlayerSync's default.</summary>
    public static TextureSourceSetting FromPlayerSync(int? rawMode, bool pairOverride)
    {
        TextureSourceMode? mode = rawMode == null ? PlayerSyncDefault
            : Enum.IsDefined(typeof(TextureSourceMode), rawMode.Value) ? (TextureSourceMode)rawMode.Value : null;
        return new TextureSourceSetting { GlobalMode = mode, PairOverride = pairOverride };
    }

    public string Describe() => Effective switch
    {
        TextureSourceMode.AlwaysSourceQuality when PairOverride => "Source quality (this pair is on PlayerSync's Override UIDs list).",
        TextureSourceMode.AlwaysSourceQuality => "Source quality (PlayerSync: Always use source quality).",
        TextureSourceMode.CompressedNewDownloads => "Compressed for new downloads (PlayerSync setting). Textures downloaded earlier may be originals.",
        TextureSourceMode.AlwaysCompressed => "Always compressed (PlayerSync setting). PlayerSync downloads its compressed substitutes where they exist.",
        _ => "Unknown (PlayerSync's texture compression setting could not be read).",
    };
}

/// <summary>
/// Mirrors <c>MareSynchronos.Services.SourceQualityExport.SourceQualityFileStatus</c> in the PlayerSync source-quality export
/// IPC (API version 1). Parsed by name; unknown names become <see cref="Unknown"/>.
/// </summary>
public enum StagedOriginalStatus
{
    Unknown,
    Pending,
    CachedOriginal,
    Downloaded,
    NotPermitted,
    Forbidden,
    NotOnServer,
    Unavailable,
    HashMismatch,
    Failed,
    Cancelled,
}

/// <summary>One file of a source-quality download, as PlayerSync reported it.</summary>
public sealed record StagedOriginal
{
    public required string Hash { get; init; }
    public StagedOriginalStatus Status { get; init; }
    public string? Path { get; init; }
    public long Size { get; init; }
    public bool Sha1Verified { get; init; }
    public string? Message { get; init; }

    public bool IsAvailable => Status is StagedOriginalStatus.Downloaded or StagedOriginalStatus.CachedOriginal;
}

/// <summary>The outcome of an export-scoped source-quality download (PlayerSync.SourceQualityExport.GetStatus).</summary>
public sealed record SourceQualityDownloadResult
{
    /// <summary>Running, Completed, Cancelled, Failed, or Unknown.</summary>
    public string State { get; init; } = "Unknown";
    public string? Error { get; init; }
    public IReadOnlyList<StagedOriginal> Files { get; init; } = [];

    public bool IsFinished => State is not ("Running" or "Unknown");

    public int Requested => Files.Count;

    public int Count(StagedOriginalStatus status) => Files.Count(f => f.Status == status);

    public int Finished => Files.Count(f => f.Status is not (StagedOriginalStatus.Pending or StagedOriginalStatus.Unknown));

    public StagedOriginal? Find(string hash) => Files.FirstOrDefault(f => string.Equals(f.Hash, hash, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>Parses PlayerSync's status JSON. Malformed input yields a Failed result instead of throwing.</summary>
    public static SourceQualityDownloadResult Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty);
            var root = doc.RootElement;
            var files = new List<StagedOriginal>();
            if (root.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in list.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("hash", out var h) || h.GetString() is not { Length: > 0 } hash)
                        continue;
                    files.Add(new StagedOriginal
                    {
                        Hash = hash,
                        Status = f.TryGetProperty("status", out var st) && Enum.TryParse<StagedOriginalStatus>(st.GetString(), out var parsed) ? parsed : StagedOriginalStatus.Unknown,
                        Path = f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                        Size = f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var size) ? size : 0,
                        Sha1Verified = f.TryGetProperty("sha1Verified", out var v) && v.ValueKind == JsonValueKind.True,
                        Message = f.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                    });
                }
            }

            return new SourceQualityDownloadResult
            {
                State = root.TryGetProperty("state", out var s) && s.GetString() is { Length: > 0 } state ? state : "Unknown",
                Error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                Files = files,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new SourceQualityDownloadResult { State = "Failed", Error = "PlayerSync returned an unreadable status: " + ex.Message };
        }
    }

    public string Describe()
    {
        if (State == "Cancelled")
            return "The download of originals was cancelled; nothing was kept.";
        if (State == "Failed")
            return "The download of originals failed: " + (Error ?? "unknown error") + ".";
        var parts = new List<string>();
        void Part(int n, string text)
        {
            if (n > 0)
                parts.Add(n.ToString(CultureInfo.InvariantCulture) + " " + text);
        }

        Part(Count(StagedOriginalStatus.Downloaded), "downloaded");
        Part(Count(StagedOriginalStatus.CachedOriginal), "already on this computer");
        Part(Count(StagedOriginalStatus.Forbidden), "forbidden by the server");
        Part(Count(StagedOriginalStatus.NotOnServer), "no longer on the server");
        Part(Count(StagedOriginalStatus.NotPermitted), "not permitted");
        Part(Count(StagedOriginalStatus.HashMismatch), "failed the SHA-1 check");
        Part(Count(StagedOriginalStatus.Unavailable) + Count(StagedOriginalStatus.Failed) + Count(StagedOriginalStatus.Unknown), "could not be retrieved");
        return $"Originals requested from PlayerSync: {Requested}" + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")." : ".");
    }
}

/// <summary>
/// The originals to request: textures that are only available as PlayerSync's compressed substitute (or whose substitute is
/// invalid). Only files that are synced and not forbidden qualify, so nothing PlayerSync's permission filters or the server
/// withheld is ever asked for.
/// </summary>
public sealed class SourceQualityRequestPlan
{
    private SourceQualityRequestPlan(IReadOnlyList<string> hashes) => Hashes = hashes;

    public IReadOnlyList<string> Hashes { get; }

    public bool IsNeeded => Hashes.Count > 0;

    public static SourceQualityRequestPlan From(AnalysisResult analysis)
    {
        var redirected = analysis.Character.CompressionRedirects;
        var hashes = analysis.Assets
            .Where(a => a.Representation == ContentRepresentation.CompressedAlternate
                        || (a.Status == AssetStatus.InvalidContent && redirected.ContainsKey(a.Hash)))
            .Where(a => a.Status is AssetStatus.Available or AssetStatus.InvalidContent)
            .Select(a => a.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SourceQualityRequestPlan(hashes);
    }
}

/// <summary>
/// Offers the originals PlayerSync downloaded into this export's staging folder. Only files PlayerSync reported as
/// Downloaded and that lie inside <paramref name="stagingDirectory"/> are offered; the analyzer still verifies their SHA-1.
/// Originals PlayerSync found already on this computer are found by the normal cache locators.
/// </summary>
public sealed class StagedOriginalsLocator(string stagingDirectory, SourceQualityDownloadResult result) : IAssetLocator
{
    public string Name => "PlayerSync source-quality download";

    public IEnumerable<LocatedCandidate> Locate(string hash, string extension)
    {
        if (result.Find(hash) is not { Status: StagedOriginalStatus.Downloaded, Path: { Length: > 0 } path })
            yield break;
        if (IO.PathSafety.IsUnderRoot(stagingDirectory, path))
            yield return new LocatedCandidate(path, AssetProvenance.SourceQualityDownload);
    }
}

/// <summary>
/// A folder the exporter owns for originals downloaded by PlayerSync: <c>&lt;export folder&gt;/.psme-originals-&lt;id&gt;/</c> with a
/// marker file. Only such folders are ever deleted.
/// </summary>
public sealed class OriginalsStaging : IDisposable
{
    public const string Prefix = ".psme-originals-";
    public const string MarkerFileName = ".psme-originals";

    private OriginalsStaging(string path) => Path = path;

    public string Path { get; }

    public static OriginalsStaging Create(string exportRoot)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(exportRoot), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(System.IO.Path.Combine(path, MarkerFileName), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return new OriginalsStaging(path);
    }

    public static bool IsOwned(string directory)
        => System.IO.Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal)
           && File.Exists(System.IO.Path.Combine(directory, MarkerFileName));

    /// <summary>Removes owned folders left behind by a crash. Other folders are never touched.</summary>
    public static void CleanupAbandoned(string exportRoot)
    {
        try
        {
            if (!Directory.Exists(exportRoot))
                return;
            foreach (var dir in Directory.EnumerateDirectories(exportRoot, Prefix + "*"))
                if (IsOwned(dir))
                    TryDelete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public void Dispose() => TryDelete(Path);

    private static void TryDelete(string dir)
    {
        try
        {
            if (IsOwned(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort; CleanupAbandoned retries later
        }
    }
}
