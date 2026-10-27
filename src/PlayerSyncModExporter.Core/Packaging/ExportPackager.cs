using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Hashing;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Meta;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Safety;

namespace PlayerSyncModExporter.Core.Packaging;

public enum ExistingPackagePolicy
{
    /// <summary>Refuse to export if the target folder already exists.</summary>
    Fail,

    /// <summary>Write to "Name (2)", "Name (3)", ... instead.</summary>
    CreateNumberedCopy,

    /// <summary>Replace the existing folder. Only allowed when it is a previous export from this tool and the user approved it.</summary>
    ReplaceExisting,
}

public sealed class ExportRequest
{
    public required AnalysisResult Analysis { get; init; }
    public required string DestinationRoot { get; init; }
    public required string PackageName { get; init; }
    public required ExportConsent Consent { get; init; }
    public bool WritePmp { get; init; } = true;
    public bool WriteMcdf { get; init; } = true;
    public bool IncludeAppearanceData { get; init; } = true;
    public bool IncludeNonPlayerObjects { get; init; }
    public ExistingPackagePolicy ExistingPolicy { get; init; } = ExistingPackagePolicy.Fail;
    public string GeneratorVersion { get; init; } = "0.0.0";
}

public enum ExportStage
{
    Preparing,
    WritingPmp,
    WritingMcdf,
    WritingManifest,
    Verifying,
    Finalizing,
    Done,
}

public sealed record ExportProgress(ExportStage Stage, long BytesDone, long BytesTotal, string? Detail);

public sealed class ExportResult
{
    public required string PackageDirectory { get; init; }
    public string? PmpPath { get; init; }
    public string? McdfPath { get; init; }
    public required string ManifestPath { get; init; }
    public required ExportManifest Manifest { get; init; }
    public required IReadOnlyList<string> VerificationNotes { get; init; }
}

public class ExportFailedException : Exception
{
    public ExportFailedException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed class InsufficientDiskSpaceException : ExportFailedException
{
    public InsufficientDiskSpaceException(long required, long available)
        : base($"Not enough free disk space: about {Format(required)} needed, {Format(available)} available.")
    {
        Required = required;
        Available = available;
    }

    public long Required { get; }
    public long Available { get; }

    internal static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        _ => $"{bytes / 1024d:0.0} KiB",
    };
}

/// <summary>
/// Builds a package in a staging directory beside the destination, verifies every written byte against
/// its content hash, and only then moves it into place. The PlayerSync cache and Penumbra are only ever read.
/// </summary>
public sealed class ExportPackager
{
    public const string ManifestFileName = "manifest.json";
    public const string AppearanceFolder = "appearance";

    private readonly IMetaManipulationConverter _metaConverter;
    private readonly IDiskSpaceProbe _diskSpace;

    public ExportPackager(IMetaManipulationConverter? metaConverter = null, IDiskSpaceProbe? diskSpace = null)
    {
        _metaConverter = metaConverter ?? new Version0MetaConverter();
        _diskSpace = diskSpace ?? new DriveInfoDiskSpaceProbe();
    }

    /// <summary>Computes how the request would be packaged without writing anything (for the preview).</summary>
    public PackagePlan Plan(ExportRequest request) => PackagePlan.Create(request.Analysis, request.IncludeNonPlayerObjects);

    public async Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        if (!request.Consent.IsComplete)
            throw new ConsentRequiredException();
        if (!request.WritePmp && !request.WriteMcdf)
            throw new ExportFailedException("Select at least one package format.");
        if (string.IsNullOrWhiteSpace(request.DestinationRoot) || !Path.IsPathRooted(request.DestinationRoot))
            throw new ExportFailedException("Choose an absolute export folder.");

        progress?.Report(new ExportProgress(ExportStage.Preparing, 0, 0, null));
        var plan = Plan(request);
        if (plan.Files.Count == 0 && plan.FileSwaps.Count == 0)
            throw new ExportFailedException("Nothing to export: no verified files are selected.");

        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var packageName = PathSafety.SanitizeFileName(request.PackageName, "PlayerSync Export");
        var finalDirectory = ResolveFinalDirectory(destinationRoot, packageName, request.ExistingPolicy);

        var mcdfFiles = request.WriteMcdf ? BuildMcdfFileList(plan) : [];
        var required = EstimateRequiredBytes(plan, request, mcdfFiles);
        if (_diskSpace.GetAvailableBytes(destinationRoot) is { } available && available < required)
            throw new InsufficientDiskSpaceException(required, available);

        var meta = ConvertMeta(request.Analysis.Character.ManipulationData);
        var manifest = BuildManifest(request, plan, packageName, meta);

        using var staging = StagingDirectory.Create(destinationRoot);
        var totalBytes = plan.TotalBytes * ((request.WritePmp ? 1 : 0) + (request.WriteMcdf ? 1 : 0));
        long done = 0;
        var stage = ExportStage.Preparing;
        var syncBytes = new SynchronousProgress(n =>
        {
            done += n;
            progress?.Report(new ExportProgress(stage, done, totalBytes, null));
        });
        if (request.WritePmp)
            manifest.Package.PmpFile = packageName + ".pmp";
        if (request.WriteMcdf)
            manifest.Package.McdfFile = packageName + ".mcdf";

        try
        {
            string? pmpPath = null, mcdfPath = null;
            if (request.WritePmp)
            {
                stage = ExportStage.WritingPmp;
                pmpPath = Path.Combine(staging.Path, manifest.Package.PmpFile!);
                await using var fs = CreateNew(pmpPath);
                await PmpWriter.WriteAsync(fs, plan, BuildPmpMetadata(request, packageName), meta.Json,
                    (file, dest, ct) => StreamingCopy.CopyVerifiedAsync(file.SourcePath, dest, file.ContentHash, syncBytes, ct),
                    () => JsonSerializer.Serialize(manifest, ExportManifest.JsonOptions), token).ConfigureAwait(false);
            }

            if (request.WriteMcdf)
            {
                stage = ExportStage.WritingMcdf;
                mcdfPath = Path.Combine(staging.Path, manifest.Package.McdfFile!);
                var data = BuildMcdfData(request, plan, mcdfFiles);
                var sources = plan.Files.ToDictionary(f => f.ContentHash, f => f.SourcePath, StringComparer.OrdinalIgnoreCase);
                await using var fs = CreateNew(mcdfPath);
                await McdfFormat.WriteAsync(fs, data,
                    (file, dest, ct) => StreamingCopy.CopyVerifiedAsync(sources[file.Hash], dest, file.Hash, syncBytes, ct),
                    token).ConfigureAwait(false);
            }

            stage = ExportStage.WritingManifest;
            progress?.Report(new ExportProgress(stage, done, totalBytes, null));
            if (request.IncludeAppearanceData)
                await WriteAppearanceAsync(staging.Path, request.Analysis.Character, manifest, token).ConfigureAwait(false);

            var manifestPath = Path.Combine(staging.Path, ManifestFileName);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ExportManifest.JsonOptions), token).ConfigureAwait(false);

            stage = ExportStage.Verifying;
            progress?.Report(new ExportProgress(stage, done, totalBytes, null));
            var notes = new List<string>();
            if (pmpPath != null)
                notes.AddRange(await VerifyPmpAsync(pmpPath, plan, token).ConfigureAwait(false));
            if (mcdfPath != null)
            {
                var (_, problems) = await McdfFormat.VerifyAsync(mcdfPath, token).ConfigureAwait(false);
                if (problems.Count > 0)
                    throw new ExportFailedException("MCDF verification failed: " + string.Join(" ", problems));
                notes.Add($"MCDF verified: {mcdfFiles.Count} payloads match their hashes.");
            }

            stage = ExportStage.Finalizing;
            progress?.Report(new ExportProgress(stage, done, totalBytes, null));
            File.Delete(Path.Combine(staging.Path, StagingDirectory.MarkerFileName));
            MoveIntoPlace(staging, finalDirectory, request.ExistingPolicy);

            progress?.Report(new ExportProgress(ExportStage.Done, done, totalBytes, finalDirectory));
            return new ExportResult
            {
                PackageDirectory = finalDirectory,
                PmpPath = pmpPath == null ? null : Path.Combine(finalDirectory, Path.GetFileName(pmpPath)),
                McdfPath = mcdfPath == null ? null : Path.Combine(finalDirectory, Path.GetFileName(mcdfPath)),
                ManifestPath = Path.Combine(finalDirectory, ManifestFileName),
                Manifest = manifest,
                VerificationNotes = notes,
            };
        }
        catch (IOException e) when (StreamingCopy.IsDiskFull(e))
        {
            throw new ExportFailedException("The disk filled up while writing the package. Nothing was kept; free some space and try again.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new ExportFailedException("Access to the export folder was denied: " + e.Message, e);
        }
        catch (SourceChangedException e)
        {
            throw new ExportFailedException(e.Message, e);
        }
        catch (InvalidDataException e)
        {
            throw new ExportFailedException("Package verification failed: " + e.Message, e);
        }
    }

    private static FileStream CreateNew(string path)
        => new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous);

    private static string ResolveFinalDirectory(string root, string name, ExistingPackagePolicy policy)
    {
        var target = Path.Combine(root, name);
        if (!Directory.Exists(target) && !File.Exists(target))
            return target;

        return policy switch
        {
            ExistingPackagePolicy.CreateNumberedCopy => PathSafety.GetUniquePath(root, name),
            ExistingPackagePolicy.ReplaceExisting when IsPreviousExport(target) => target,
            ExistingPackagePolicy.ReplaceExisting => throw new ExportFailedException(
                $"'{target}' exists but is not a previous export from this tool, so it will not be replaced. Choose another name."),
            _ => throw new ExportFailedException($"'{target}' already exists. Choose another name or allow replacing the previous export."),
        };
    }

    /// <summary>A folder may only be replaced if it carries our manifest.</summary>
    public static bool IsPreviousExport(string directory)
    {
        var manifest = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifest))
            return false;
        try
        {
            return JsonNode.Parse(File.ReadAllText(manifest))?["schema"]?.GetValue<string>() == ExportManifest.SchemaId;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void MoveIntoPlace(StagingDirectory staging, string finalDirectory, ExistingPackagePolicy policy)
    {
        string? backup = null;
        if (Directory.Exists(finalDirectory))
        {
            if (policy != ExistingPackagePolicy.ReplaceExisting || !IsPreviousExport(finalDirectory))
                throw new ExportFailedException($"'{finalDirectory}' appeared while exporting; it was not touched.");
            backup = finalDirectory + ".psme-replaced-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(finalDirectory, backup);
        }

        try
        {
            Directory.Move(staging.Path, finalDirectory);
            staging.MarkCommitted();
        }
        catch
        {
            if (backup != null && !Directory.Exists(finalDirectory))
                Directory.Move(backup, finalDirectory);
            throw;
        }

        if (backup != null)
        {
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The new package is in place; the old copy can be removed by the user.
            }
        }
    }

    private static long EstimateRequiredBytes(PackagePlan plan, ExportRequest request, IReadOnlyList<PlannedFile> mcdfFiles)
    {
        long total = 0;
        if (request.WritePmp)
            total += plan.TotalBytes;
        if (request.WriteMcdf)
            total += mcdfFiles.Sum(f => f.Size);
        // Zip/LZ4 framing, JSON and appearance data, plus a safety margin.
        return total + total / 20 + 16L * 1024 * 1024;
    }

    private (string? Json, ManifestMeta Manifest) ConvertMeta(string manipulationData)
    {
        var info = MetaManipulationCodec.Inspect(manipulationData);
        if (info.IsEmpty)
            return (null, new ManifestMeta { Present = false, Note = "The character has no meta manipulations." });

        if (_metaConverter.TryConvert(manipulationData, out var json, out var error))
        {
            var count = MetaManipulationCodec.CountEntries(json);
            return (json, new ManifestMeta
            {
                Present = true, FormatVersion = info.Version, EmbeddedInPmp = true, EmbeddedEntries = count, Converter = _metaConverter.Name,
            });
        }

        return (null, new ManifestMeta
        {
            Present = true, FormatVersion = info.Version, EmbeddedInPmp = false, Converter = _metaConverter.Name,
            Note = "Meta manipulations (e.g. height, hidden gear parts, IMC variants) could not be converted for the .pmp and are NOT applied by it. "
                   + "They are preserved unchanged in the .mcdf and appearance/penumbra-meta.b64.txt. " + error,
        });
    }

    private static List<PlannedFile> BuildMcdfFileList(PackagePlan plan)
        => plan.Files.Where(f => f.Assets.Any(a => a.Kinds.Contains(SyncObjectKind.Player))).ToList();

    private static McdfFormat.McdfData BuildMcdfData(ExportRequest request, PackagePlan plan, IReadOnlyList<PlannedFile> files)
    {
        var c = request.Analysis.Character;
        var playerRedirects = plan.Redirections.Keys.ToHashSet(StringComparer.Ordinal);
        return new McdfFormat.McdfData
        {
            Description = $"{c.Label} (exported by PlayerSync Mod Exporter, {c.CapturedAtUtc:yyyy-MM-dd})",
            GlamourerData = c.GlamourerData,
            CustomizePlusData = c.CustomizePlusData,
            ManipulationData = c.ManipulationData,
            Files = files.Select(f => new McdfFormat.McdfFile
                {
                    Hash = f.ContentHash,
                    Length = f.Size > int.MaxValue ? -1 : (int)f.Size,
                    GamePaths = f.Assets.SelectMany(a => a.GamePaths)
                        .Where(p => p.Kind == SyncObjectKind.Player && playerRedirects.Contains(p.GamePath))
                        .Select(p => p.GamePath).Distinct(StringComparer.Ordinal).ToList(),
                })
                .Where(f => f.GamePaths.Count > 0)
                .ToList(),
            FileSwaps = plan.FileSwaps.Where(s => s.Kind == SyncObjectKind.Player)
                .GroupBy(s => s.Target, StringComparer.Ordinal)
                .Select(g => new McdfFormat.McdfSwap { FileSwapPath = g.Key, GamePaths = g.Select(s => s.GamePath).ToList() })
                .ToList(),
        };
    }

    private static PmpWriter.PmpMetadata BuildPmpMetadata(ExportRequest request, string packageName)
    {
        var c = request.Analysis.Character;
        return new PmpWriter.PmpMetadata(
            Name: packageName,
            Author: "Unknown (reconstructed from PlayerSync cache)",
            Description:
            $"Reconstructed from the PlayerSync appearance of \"{c.Label}\" captured {c.CapturedAtUtc:yyyy-MM-dd HH:mm} UTC. "
            + "This is not an original mod archive; the original creators and their terms are unknown. "
            + "See export_manifest.json for the file inventory and completeness.",
            Version: c.CapturedAtUtc.ToString("yyyy.MM.dd.HHmm"),
            Tags: ["PlayerSync Export"]);
    }

    private static async Task WriteAppearanceAsync(string stagingPath, CapturedCharacter c, ExportManifest manifest, CancellationToken token)
    {
        var folder = Path.Combine(stagingPath, AppearanceFolder);
        Directory.CreateDirectory(folder);

        async Task<string?> Write(string fileName, string content)
        {
            if (string.IsNullOrEmpty(content))
                return null;
            await File.WriteAllTextAsync(Path.Combine(folder, fileName), content, token).ConfigureAwait(false);
            return $"{AppearanceFolder}/{fileName}";
        }

        manifest.Appearance.GlamourerFile = await Write("glamourer.b64.txt", c.GlamourerData).ConfigureAwait(false);
        manifest.Appearance.CustomizePlusFile = await Write("customizeplus.b64.txt", c.CustomizePlusData).ConfigureAwait(false);
        manifest.Appearance.PenumbraMetaFile = await Write("penumbra-meta.b64.txt", c.ManipulationData).ConfigureAwait(false);
    }

    private static ExportManifest BuildManifest(ExportRequest request, PackagePlan plan, string packageName,
        (string? Json, ManifestMeta Manifest) meta)
    {
        var analysis = request.Analysis;
        var c = analysis.Character;
        var archiveByAsset = plan.Files.SelectMany(f => f.Assets.Select(a => (a, f.ArchivePath))).ToDictionary(x => x.a, x => x.ArchivePath);

        var files = analysis.Assets.Select(a => new ManifestFile
        {
            Hash = a.Hash,
            ContentHash = a.ContentHash,
            Extension = a.Extension,
            Size = a.Size,
            GamePaths = a.GamePaths.Select(p => p.GamePath).Distinct(StringComparer.Ordinal).ToList(),
            ObjectKinds = a.Kinds.Select(k => k.ToString()).ToList(),
            Status = a.Status.ToString(),
            Included = archiveByAsset.ContainsKey(a),
            Provenance = a.Provenance.ToString(),
            IsOriginalContent = a.Status == AssetStatus.Available && a.IsOriginalContent,
            SharingStatus = a.Sharing.ToString(),
            ArchivePath = archiveByAsset.GetValueOrDefault(a),
            Note = a.Note,
        }).ToList();

        var includedAssets = analysis.Assets.Where(archiveByAsset.ContainsKey).ToList();
        var summary = new ManifestSummary
        {
            CapturedFiles = analysis.Assets.Count,
            IncludedFiles = plan.Files.Count,
            IncludedBytes = plan.TotalBytes,
            DuplicateReferencesMerged = c.Files.Count - analysis.Assets.Count(a => a.Status != AssetStatus.NotSynced),
            Missing = analysis.Count(AssetStatus.Missing),
            HashMismatch = analysis.Count(AssetStatus.HashMismatch),
            Unreadable = analysis.Count(AssetStatus.Unreadable),
            NotSynced = analysis.Count(AssetStatus.NotSynced),
            ForbiddenByServer = analysis.Count(AssetStatus.ForbiddenByServer),
            InvalidMetadata = analysis.Count(AssetStatus.InvalidMetadata),
            ExcludedByUser = analysis.Assets.Count(a => a.IsExportable && !archiveByAsset.ContainsKey(a)),
            CompressedAlternatesIncluded = includedAssets.Count(a => !a.IsOriginalContent),
            FileSwaps = plan.FileSwaps.Count,
        };

        var reasons = new List<string>();
        void Reason(int n, string text)
        {
            if (n > 0)
                reasons.Add($"{n} {text}");
        }

        Reason(summary.Missing, "file(s) are missing from the local cache.");
        Reason(summary.HashMismatch, "file(s) are corrupt (hash mismatch) and were not exported.");
        Reason(summary.Unreadable, "file(s) could not be read.");
        Reason(summary.InvalidMetadata, "entry(ies) in the character data are malformed.");
        Reason(summary.NotSynced, "file(s) were not synced to you by PlayerSync (permissions/filters).");
        Reason(summary.ForbiddenByServer, "file(s) are forbidden by the PlayerSync server.");
        Reason(summary.ExcludedByUser, "available file(s) were excluded by your selection.");
        Reason(summary.CompressedAlternatesIncluded, "texture(s) are PlayerSync compressed alternates, not the original files.");
        Reason(analysis.FileSwaps.Count - plan.FileSwaps.Count, "file swap(s) were left out.");
        if (meta.Manifest.Present && !meta.Manifest.EmbeddedInPmp && request.WritePmp)
            reasons.Add("Meta manipulations are not embedded in the .pmp (kept in the .mcdf and appearance folder).");

        var manifest = new ExportManifest
        {
            Generator = new ManifestGenerator { Version = request.GeneratorVersion },
            Package = new ManifestPackage
            {
                Name = packageName,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Formats = [.. request.WritePmp ? new[] { "pmp" } : [], .. request.WriteMcdf ? new[] { "mcdf" } : []],
            },
            Character = new ManifestCharacter
            {
                Label = c.Label, CapturedAtUtc = c.CapturedAtUtc, Source = c.Source, SourceVersion = c.SourceVersion,
            },
            Completeness = new ManifestCompleteness
            {
                Status = reasons.Count == 0 ? CompletenessStatus.Complete : CompletenessStatus.Partial, Reasons = reasons,
            },
            Consent = new ManifestConsent
            {
                PermissionConfirmed = request.Consent.PermissionConfirmed,
                RedistributionAcknowledged = request.Consent.RedistributionAcknowledged,
                UnknownSharingStatusAcknowledged = request.Consent.UnknownSharingStatusAcknowledged,
                ConfirmedAtUtc = request.Consent.ConfirmedAtUtc,
            },
            Summary = summary,
            Files = files,
            FileSwaps = analysis.FileSwaps.Select(s => new ManifestFileSwap
            {
                GamePath = s.GamePath, Target = s.Target, ObjectKind = s.Kind.ToString(), Included = plan.FileSwaps.Contains(s),
            }).ToList(),
            Meta = meta.Manifest,
            Warnings = [.. analysis.Warnings, .. plan.Warnings],
        };
        return manifest;
    }

    /// <summary>Re-opens the written archive and checks structure and every payload hash.</summary>
    public static async Task<List<string>> VerifyPmpAsync(string pmpPath, PackagePlan plan, CancellationToken token)
    {
        var notes = new List<string>();
        await using var fs = new FileStream(pmpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var meta = zip.GetEntry(PmpWriter.MetaEntry) ?? throw new InvalidDataException("meta.json is missing.");
        await using (var s = meta.Open())
        {
            var node = await JsonNode.ParseAsync(s, cancellationToken: token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(node?["Name"]?.GetValue<string>()))
                throw new InvalidDataException("meta.json has no Name (Penumbra rejects that).");
        }

        var def = zip.GetEntry(PmpWriter.DefaultModEntry) ?? throw new InvalidDataException("default_mod.json is missing.");
        JsonObject filesNode;
        await using (var s = def.Open())
        {
            var node = await JsonNode.ParseAsync(s, cancellationToken: token).ConfigureAwait(false);
            filesNode = node?["Files"] as JsonObject ?? throw new InvalidDataException("default_mod.json has no Files object.");
        }

        foreach (var (gamePath, value) in filesNode)
        {
            var rel = value?.GetValue<string>()?.Replace('\\', '/');
            if (rel == null || zip.GetEntry(rel) == null)
                throw new InvalidDataException($"default_mod.json maps '{gamePath}' to a file that is not in the archive.");
        }

        foreach (var file in plan.Files)
        {
            var entry = zip.GetEntry(file.ArchivePath) ?? throw new InvalidDataException($"{file.ArchivePath} is missing from the archive.");
            if (entry.Length != file.Size)
                throw new InvalidDataException($"{file.ArchivePath} has {entry.Length} bytes, expected {file.Size}.");
            await using var s = entry.Open();
            var hash = await ContentHash.ComputeAsync(s, token).ConfigureAwait(false);
            if (!string.Equals(hash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{file.ArchivePath} hashes to {hash}, expected {file.ContentHash}.");
        }

        if (zip.Entries.Any(e => e.FullName.Contains("..", StringComparison.Ordinal) || e.FullName.StartsWith('/')))
            throw new InvalidDataException("The archive contains an unsafe entry name.");

        notes.Add($"PMP verified: {plan.Files.Count} payloads, {filesNode.Count} redirections, {plan.FileSwaps.Count} file swaps.");
        return notes;
    }

    /// <summary>Reports progress inline (Progress&lt;T&gt; would post to the thread pool and reorder counts).</summary>
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
