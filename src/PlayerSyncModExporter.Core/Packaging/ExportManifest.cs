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
    public ManifestCompleteness Completeness { get; set; } = new();
    public ManifestConsent Consent { get; set; } = new();
    public ManifestSummary Summary { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = [];
    public List<ManifestFileSwap> FileSwaps { get; set; } = [];
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
    public string SharingStatus { get; set; } = string.Empty;
    public string? ArchivePath { get; set; }
    public string? Note { get; set; }
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
