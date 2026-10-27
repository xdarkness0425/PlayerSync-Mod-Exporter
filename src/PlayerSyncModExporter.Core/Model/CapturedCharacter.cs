namespace PlayerSyncModExporter.Core.Model;

/// <summary>
/// Mirrors PlayerSync's <c>MareSynchronos.API.Data.Enum.ObjectKind</c> (MareAPI/MareSynchronosAPI/Data/Enum/ObjectKind.cs).
/// Numeric values must stay identical because the adapter converts by value.
/// </summary>
public enum SyncObjectKind
{
    Player = 0,
    MinionOrMount = 1,
    Companion = 2,
    Pet = 3,
}

/// <summary>A content-addressed file replacement: the file with SHA-1 <see cref="Hash"/> replaces every game path listed.</summary>
public sealed record CapturedFileReplacement(SyncObjectKind Kind, string Hash, IReadOnlyList<string> GamePaths);

/// <summary>A Penumbra file swap: each game path is redirected to another game path (<see cref="SwapTarget"/>), no payload needed.</summary>
public sealed record CapturedFileSwap(SyncObjectKind Kind, string SwapTarget, IReadOnlyList<string> GamePaths);

/// <summary>Why a captured file replacement was not delivered to this client.</summary>
public enum NotSyncedReason
{
    /// <summary>Removed by PlayerSync's own permission and filter rules (Pair.RemoveNotSyncedFiles).</summary>
    PlayerSyncFilter,
}

public sealed record NotSyncedFile(CapturedFileReplacement File, NotSyncedReason Reason);

/// <summary>
/// An immutable, PlayerSync-independent snapshot of one synchronized character's appearance data,
/// taken at a single moment at the user's request. Contains no account identifiers or credentials.
/// </summary>
public sealed class CapturedCharacter
{
    /// <summary>User-facing label (defaults to the character name). Used for the package name.</summary>
    public required string Label { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Name and version of the plugin the data came from, e.g. "PlayerSync 1.15.5.9".</summary>
    public string Source { get; init; } = "PlayerSync";

    public string? SourceVersion { get; init; }

    /// <summary>File replacements PlayerSync applies to this character after its permission filters.</summary>
    public IReadOnlyList<CapturedFileReplacement> Files { get; init; } = [];

    public IReadOnlyList<CapturedFileSwap> FileSwaps { get; init; } = [];

    /// <summary>File replacements present in the received data but filtered out by PlayerSync. Never exported.</summary>
    public IReadOnlyList<NotSyncedFile> NotSyncedFiles { get; init; } = [];

    /// <summary>Original hash -> compressed alternate hash that PlayerSync applied instead (PairHandler.ActiveCompressionRedirects).</summary>
    public IReadOnlyDictionary<string, string> CompressionRedirects { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hashes the PlayerSync file server marked as forbidden (FileTransferOrchestrator.ForbiddenTransfers).</summary>
    public IReadOnlySet<string> ServerForbiddenHashes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Glamourer state (base64) for the player object, as received through PlayerSync.</summary>
    public string GlamourerData { get; init; } = string.Empty;

    /// <summary>Customize+ data (base64) for the player object, as received through PlayerSync.</summary>
    public string CustomizePlusData { get; init; } = string.Empty;

    /// <summary>Penumbra meta manipulations, in Penumbra's compressed base64 format.</summary>
    public string ManipulationData { get; init; } = string.Empty;

    /// <summary>Warnings produced while reading the source data (malformed entries etc.).</summary>
    public IReadOnlyList<string> CaptureWarnings { get; init; } = [];
}
