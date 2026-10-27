using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlayerSyncModExporter.Core.Packaging;

public enum CompletenessStatus
{
    /// <summary>Every file PlayerSync applies to this character is included with its original content, and meta data is embedded.</summary>
    Complete,

    /// <summary>The package is valid but does not fully reproduce the captured appearance. See the reasons.</summary>
    Partial,
}

/// <summary>Root of manifest.json. Schema documented in docs/EXPORT_FORMAT.md.</summary>
public sealed class ExportManifest
{
    public const string SchemaId = "playersync-mod-exporter/manifest";
    public const int CurrentSchemaVersion = 1;

    public string Schema { get; set; } = SchemaId;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public ManifestGenerator Generator { get; set; } = new();
    public ManifestPackage Package { get; set; } = new();
    public ManifestCharacter Character { get; set; } = new();
    public ManifestTextureSource TextureSource { get; set; } = new();
    public ManifestCompleteness Completeness { get; set; } = new();
    public ManifestConsent Consent { get; set; } = new();
    public ManifestSummary Summary { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = [];
    public List<ManifestFileSwap> FileSwaps { get; set; } = [];
    public ManifestMetadataRecovery Metadata { get; set; } = new();
    public ManifestMeta Meta { get; set; } = new();
    public ManifestAppearance Appearance { get; set; } = new();
    public List<string> Warnings { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class ManifestGenerator
{
    public string Name { get; set; } = "PlayerSync Mod Exporter";
    public string Version { get; set; } = "0.0.0";
}

public sealed class ManifestPackage
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public List<string> Formats { get; set; } = [];
    public string? PmpFile { get; set; }
    public string? McdfFile { get; set; }

    /// <summary>Always "reconstructed": built from cached content, never an original creator archive.</summary>
    public string Origin { get; set; } = "reconstructed";
    public string OriginNote { get; set; } =
        "This package was reconstructed from content-addressed files in a local PlayerSync cache. It is not an original mod archive from the creators "
        + "and contains no option groups, original folder structure or creator metadata.";
}

public sealed class ManifestCharacter
{
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceVersion { get; set; }
}

/// <summary>PlayerSync's texture compression setting for the pair at capture time, and any source-quality refresh.</summary>
public sealed class ManifestTextureSource
{
    /// <summary>AlwaysSourceQuality, CompressedNewDownloads, AlwaysCompressed, or null when it could not be read.</summary>
    public string? EffectiveMode { get; set; }
    public string? GlobalMode { get; set; }
    public bool PairOverride { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Present only if originals were requested from PlayerSync for this export.</summary>
    public ManifestSourceQualityDownload? Download { get; set; }
}

public sealed class ManifestSourceQualityDownload
{
    public string State { get; set; } = string.Empty;
    public int OriginalsRequested { get; set; }
    public int OriginalsDownloaded { get; set; }
    public int OriginalsAlreadyLocal { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Every requested original that PlayerSync could not provide, with the reason it gave.</summary>
    public List<ManifestSourceQualityFailure> NotRetrieved { get; set; } = [];
}

public sealed class ManifestSourceQualityFailure
{
    public string Hash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public sealed class ManifestCompleteness
{
    public CompletenessStatus Status { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class ManifestConsent
{
    public bool PermissionConfirmed { get; set; }
    public bool RedistributionAcknowledged { get; set; }
    public bool UnknownSharingStatusAcknowledged { get; set; }
    public DateTimeOffset ConfirmedAtUtc { get; set; }
    public string SharingNotice { get; set; } =
        "The redistribution terms of the individual mods are unknown to this tool. Every file is marked sharingStatus=Unknown unless known to be restricted. "
        + "Do not share this package unless you have the permission of the character owner and of the mod creators.";
}

public sealed class ManifestSummary
{
    public int CapturedFiles { get; set; }
    public int IncludedFiles { get; set; }
    public long IncludedBytes { get; set; }
    public int DuplicateReferencesMerged { get; set; }
    public int Missing { get; set; }
    public int HashMismatch { get; set; }
    public int Unreadable { get; set; }
    public int NotSynced { get; set; }
    public int ForbiddenByServer { get; set; }
    public int InvalidMetadata { get; set; }
    public int ExcludedByUser { get; set; }
    public int CompressedAlternatesIncluded { get; set; }
    public int InvalidContent { get; set; }
    public int OriginalsFromInstalledMods { get; set; }
    public int FileSwaps { get; set; }
}

public sealed class ManifestFile
{
    public string Hash { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public List<string> GamePaths { get; set; } = [];
    public List<string> ObjectKinds { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public bool Included { get; set; }
    public string Provenance { get; set; } = string.Empty;
    public bool IsOriginalContent { get; set; }

    /// <summary>Original, OriginalFromInstalledMod or CompressedAlternate (see docs/EXPORT_FORMAT.md).</summary>
    public string? Representation { get; set; }

    /// <summary>Which hash the exported bytes were verified against: "hash" (the referenced file) or "compressedAlternateHash".</summary>
    public string? VerifiedAgainst { get; set; }
    public ManifestTexture? Texture { get; set; }
    public string SharingStatus { get; set; } = string.Empty;
    public string? ArchivePath { get; set; }
    public string? Note { get; set; }

    /// <summary>Per game path: which installed mod and option this file was matched to, if any.</summary>
    public List<ManifestAttribution> Attribution { get; set; } = [];
}

/// <summary>A value plus where it came from: Recovered (authoritative), Inferred (derived, labeled) or Unavailable.</summary>
public sealed class ClassifiedField
{
    public string? Value { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class ManifestAttribution
{
    public string GamePath { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string? ModName { get; set; }
    public string? ModFolder { get; set; }
    public string? Group { get; set; }
    public List<string> Options { get; set; } = [];

    /// <summary>"default" (mod's default container), "group" (reproduced option group) or "unattributed".</summary>
    public string Placement { get; set; } = "unattributed";
    public List<string> CandidateMods { get; set; } = [];
    public ClassifiedField InferredCategory { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class ManifestMetadataRecovery
{
    public string Method { get; set; } =
        "PlayerSync transfers only content hashes and game paths, never mod names or options. A file is attributed to an installed mod only when "
        + "that mod (in your local Penumbra mod directory, read-only) redirects the same game path to a file with the identical SHA-1.";
    public bool LibraryAvailable { get; set; }
    public int ModsIndexed { get; set; }
    public int LibraryProblems { get; set; }
    public int AttributedPaths { get; set; }
    public int AmbiguousPaths { get; set; }
    public int UnattributedPaths { get; set; }
    public List<ManifestSourceMod> SourceMods { get; set; } = [];

    /// <summary>Information that cannot be recovered from the accessible data, and why.</summary>
    public List<string> Unavailable { get; set; } = [];
}

public sealed class ManifestSourceMod
{
    public ClassifiedField Name { get; set; } = new();
    public ClassifiedField Author { get; set; } = new();
    public ClassifiedField Description { get; set; } = new();
    public ClassifiedField Version { get; set; } = new();
    public ClassifiedField Website { get; set; } = new();
    public string Folder { get; set; } = string.Empty;
    public int FileVersion { get; set; }
    public int MatchedPaths { get; set; }
    public List<ManifestSourceGroup> Groups { get; set; } = [];
}

public sealed class ManifestSourceGroup
{
    public ClassifiedField Name { get; set; } = new();
    public ClassifiedField Type { get; set; } = new();

    /// <summary>The mod author's default selection (option names), from the installed copy.</summary>
    public ClassifiedField AuthorDefault { get; set; } = new();

    /// <summary>The options this character appears to use, from which files matched.</summary>
    public ClassifiedField InferredSelection { get; set; } = new();
    public List<string> ReproducedOptions { get; set; } = [];
    public int TotalOptions { get; set; }
    public string? PackageGroup { get; set; }
}

public sealed class ManifestTexture
{
    public bool HeaderValid { get; set; }
    public string? Format { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipLevels { get; set; }
    public int ArraySize { get; set; }
    public string? Problem { get; set; }
}

public sealed class ManifestFileSwap
{
    public string GamePath { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public bool Included { get; set; }
}

public sealed class ManifestMeta
{
    public bool Present { get; set; }
    public int? FormatVersion { get; set; }
    public bool EmbeddedInPmp { get; set; }
    public int EmbeddedEntries { get; set; }
    public string? Converter { get; set; }
    public string? Note { get; set; }
}

public sealed class ManifestAppearance
{
    public string? GlamourerFile { get; set; }
    public string? CustomizePlusFile { get; set; }
    public string? PenumbraMetaFile { get; set; }
}
