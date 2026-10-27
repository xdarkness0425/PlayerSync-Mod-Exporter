namespace PlayerSyncModExporter.Core.Model;

public enum AssetStatus
{
    /// <summary>A local file was found and its SHA-1 matches the expected content hash.</summary>
    Available,

    /// <summary>No local copy exists (never downloaded, evicted, or deleted).</summary>
    Missing,

    /// <summary>A local copy exists but its content does not match the expected hash.</summary>
    HashMismatch,

    /// <summary>A local copy exists but could not be read (locked, permissions, I/O error).</summary>
    Unreadable,

    /// <summary>PlayerSync's permission or filter rules prevented this file from being synced to you.</summary>
    NotSynced,

    /// <summary>The PlayerSync file server marked this file as forbidden to transfer.</summary>
    ForbiddenByServer,

    /// <summary>The captured entry itself is malformed (bad hash or no valid game paths).</summary>
    InvalidMetadata,
}

public enum AssetProvenance
{
    None,

    /// <summary>Found in PlayerSync's cache folder (content downloaded from the PlayerSync file server).</summary>
    PlayerSyncCache,

    /// <summary>Found inside your own Penumbra mod directory (PlayerSync indexes those files too).</summary>
    PenumbraModDirectory,
}

public enum SharingStatus
{
    /// <summary>The exporter cannot determine the creator's redistribution terms. This is the default for everything.</summary>
    Unknown,

    /// <summary>Distribution is known to be restricted (e.g. the PlayerSync server forbade the transfer).</summary>
    Restricted,
}

public sealed record AssetGamePath(SyncObjectKind Kind, string GamePath);

/// <summary>One unique piece of content (keyed by expected hash) and everything known about it locally.</summary>
public sealed class AssetEntry
{
    /// <summary>The hash PlayerSync's character data refers to (upper-case SHA-1 hex).</summary>
    public required string Hash { get; init; }

    /// <summary>File extension without dot, derived from the game paths (e.g. "tex", "mdl").</summary>
    public required string Extension { get; init; }

    public required IReadOnlyList<AssetGamePath> GamePaths { get; init; }

    public AssetStatus Status { get; set; }

    /// <summary>Absolute source path of the verified local copy (read-only use).</summary>
    public string? SourcePath { get; set; }

    /// <summary>SHA-1 of the bytes that will actually be exported. Differs from <see cref="Hash"/> for compressed alternates.</summary>
    public string? ContentHash { get; set; }

    public long Size { get; set; }

    public AssetProvenance Provenance { get; set; }

    /// <summary>False when the local bytes are a PlayerSync server-generated compressed alternate rather than the original file.</summary>
    public bool IsOriginalContent { get; set; } = true;

    public SharingStatus Sharing { get; set; } = SharingStatus.Unknown;

    /// <summary>User choice. Only exportable entries can be included.</summary>
    public bool Include { get; set; }

    /// <summary>Human-readable explanation for the status (shown in the UI and the manifest).</summary>
    public string? Note { get; set; }

    public bool IsExportable => Status == AssetStatus.Available && Sharing != SharingStatus.Restricted && SourcePath != null;

    public IEnumerable<SyncObjectKind> Kinds => GamePaths.Select(p => p.Kind).Distinct();

    public string DisplayName => GamePaths.Count > 0 ? GamePaths[0].GamePath : Hash;
}
