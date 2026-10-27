using System.Collections.Concurrent;
using PlayerSyncModExporter.Core.Hashing;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Metadata;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Textures;

namespace PlayerSyncModExporter.Core.Analysis;

public sealed record AnalysisProgress(int Completed, int Total, string? Current);

public sealed record ValidFileSwap(SyncObjectKind Kind, string GamePath, string Target);

public sealed class AnalysisResult
{
    public required CapturedCharacter Character { get; init; }

    public required IReadOnlyList<AssetEntry> Assets { get; init; }

    public required IReadOnlyList<ValidFileSwap> FileSwaps { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public DateTimeOffset AnalyzedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Attribution of files to installed mods (original names and option groups), if a mod library was given.</summary>
    public MetadataRecoveryResult Metadata { get; init; } = MetadataRecoveryResult.None;

    public int Count(AssetStatus status) => Assets.Count(a => a.Status == status);

    public long AvailableBytes => Assets.Where(a => a.IsExportable).Sum(a => a.Size);

    /// <summary>True when every file PlayerSync would apply has a verified local copy with original content.</summary>
    public bool AllRequiredAvailable
        => Assets.Where(a => a.Status != AssetStatus.NotSynced).All(a => a.IsExportable && a.IsOriginalContent);
}

public sealed class AssetAnalyzerOptions
{
    /// <summary>Include minion, mount, pet and companion files by default (Player files are always preselected).</summary>
    public bool PreselectNonPlayerObjects { get; init; }

    public int MaxParallelism { get; init; } = 4;

    /// <summary>Index of the local Penumbra mod directory, used to recover original mod names and option groups (read-only).</summary>
    public ModLibraryIndex? ModLibrary { get; init; }
}

/// <summary>
/// Resolves each captured content hash to a verified local file. Every candidate is re-hashed
/// (SHA-1, streaming) before it is reported as available; nothing is ever written or moved.
/// </summary>
public sealed class AssetAnalyzer
{
    private readonly IReadOnlyList<IAssetLocator> _locators;
    private readonly FileHashCache _hashCache;

    public AssetAnalyzer(IReadOnlyList<IAssetLocator> locators, FileHashCache? hashCache = null)
    {
        _locators = locators;
        _hashCache = hashCache ?? new FileHashCache();
    }

    public async Task<AnalysisResult> AnalyzeAsync(CapturedCharacter character, AssetAnalyzerOptions? options = null,
        IProgress<AnalysisProgress>? progress = null, CancellationToken token = default)
    {
        options ??= new AssetAnalyzerOptions();
        var warnings = new ConcurrentBag<string>(character.CaptureWarnings);
        var entries = new List<AssetEntry>();
        var work = new List<AssetEntry>();

        // Group by expected content hash: identical content referenced by several paths or objects is exported once.
        foreach (var group in character.Files.GroupBy(f => f.Hash.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var gamePaths = NormalizeGamePaths(group, warnings);
            var hash = group.Key;
            if (!ContentHash.IsValid(hash))
            {
                entries.Add(new AssetEntry
                {
                    Hash = string.IsNullOrEmpty(hash) ? "(empty)" : hash,
                    Extension = gamePaths.Count > 0 ? PathSafety.GetExtension(gamePaths[0].GamePath) : string.Empty,
                    GamePaths = gamePaths,
                    Status = AssetStatus.InvalidMetadata,
                    Note = "The character data contains an invalid content hash for this entry.",
                });
                continue;
            }

            hash = ContentHash.Normalize(hash);
            if (gamePaths.Count == 0)
            {
                entries.Add(new AssetEntry
                {
                    Hash = hash, Extension = string.Empty, GamePaths = gamePaths, Status = AssetStatus.InvalidMetadata,
                    Note = "None of the game paths for this file are valid.",
                });
                continue;
            }

            var extensions = gamePaths.Select(p => PathSafety.GetExtension(p.GamePath)).Distinct(StringComparer.Ordinal).ToList();
            var entry = new AssetEntry { Hash = hash, Extension = extensions[0], GamePaths = gamePaths };
            if (extensions.Count > 1)
                entry.Note = $"Game paths disagree on the file type ({string.Join(", ", extensions)}); using .{extensions[0]}.";

            if (character.ServerForbiddenHashes.Contains(hash))
            {
                entry.Status = AssetStatus.ForbiddenByServer;
                entry.Sharing = SharingStatus.Restricted;
                entry.Note = "The PlayerSync file server marked this file as forbidden to transfer. It will not be exported.";
                entries.Add(entry);
                continue;
            }

            entries.Add(entry);
            work.Add(entry);
        }

        var knownHashes = new HashSet<string>(entries.Select(e => e.Hash), StringComparer.OrdinalIgnoreCase);
        foreach (var group in character.NotSyncedFiles.GroupBy(f => f.File.Hash.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (knownHashes.Contains(group.Key))
                continue;
            var gamePaths = NormalizeGamePaths(group.Select(g => g.File), warnings);
            entries.Add(new AssetEntry
            {
                Hash = ContentHash.IsValid(group.Key) ? ContentHash.Normalize(group.Key) : group.Key,
                Extension = gamePaths.Count > 0 ? PathSafety.GetExtension(gamePaths[0].GamePath) : string.Empty,
                GamePaths = gamePaths,
                Status = AssetStatus.NotSynced,
                Note = "PlayerSync did not sync this file to you because of pair permissions or your sound/animation/VFX filters. It is never exported.",
            });
        }

        var completed = 0;
        progress?.Report(new AnalysisProgress(0, work.Count, null));
        await Parallel.ForEachAsync(work, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
            CancellationToken = token,
        }, async (entry, ct) =>
        {
            await ResolveAsync(entry, character, options.ModLibrary, ct).ConfigureAwait(false);
            var done = Interlocked.Increment(ref completed);
            progress?.Report(new AnalysisProgress(done, work.Count, entry.DisplayName));
        }).ConfigureAwait(false);

        var metadata = options.ModLibrary == null
            ? MetadataRecoveryResult.None
            : await MetadataRecoverer.RecoverAsync(entries, options.ModLibrary, _hashCache, token).ConfigureAwait(false);

        foreach (var entry in entries)
            entry.Include = entry.IsExportable
                            && (entry.Kinds.Contains(SyncObjectKind.Player) || options.PreselectNonPlayerObjects);

        return new AnalysisResult
        {
            Character = character,
            Assets = entries
                .OrderBy(e => e.Status)
                .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
                .ToList(),
            FileSwaps = NormalizeSwaps(character.FileSwaps, warnings),
            Warnings = warnings.Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList(),
            Metadata = metadata,
        };
    }

    private async Task ResolveAsync(AssetEntry entry, CapturedCharacter character, ModLibraryIndex? library, CancellationToken token)
    {
        await ResolveContentAsync(entry, character, library, token).ConfigureAwait(false);
        if (entry.Status != AssetStatus.Available || entry.Extension != "tex")
            return;

        entry.Texture = await TexFileValidator.CheckFileAsync(entry.SourcePath!, token).ConfigureAwait(false);
        if (entry.Texture.Valid)
            return;

        if (entry.Representation == ContentRepresentation.CompressedAlternate)
        {
            // A substitute is only worth exporting if it is demonstrably a texture.
            entry.Status = AssetStatus.InvalidContent;
            entry.Note = Append(entry.Note, "The compressed substitute is not a valid texture (" + entry.Texture.Problem + ") and is not exported.");
        }
        else
        {
            // The original bytes are what the other player uses; keep them unchanged even if the header is unusual.
            entry.Note = Append(entry.Note, "Texture header not recognized (" + entry.Texture.Problem + "); the original bytes are exported unchanged.");
        }
    }

    private async Task ResolveContentAsync(AssetEntry entry, CapturedCharacter character, ModLibraryIndex? library, CancellationToken token)
    {
        var outcome = await VerifyCandidatesAsync(_locators, entry.Hash, entry.Extension, token).ConfigureAwait(false);
        if (outcome.Match is { } match)
        {
            Apply(entry, match, entry.Hash, ContentRepresentation.Original);
            return;
        }

        // PlayerSync's index may not list a file that an installed mod contains (not scanned yet, scanning off). Look the game
        // paths up in the installed mods and accept a file only if its SHA-1 is the referenced hash.
        if (library != null)
        {
            var installed = new DelegateLocator("installed mods", (_, _) => entry.GamePaths
                .SelectMany(p => library.Lookup(p.GamePath))
                .Select(r => new LocatedCandidate(r.FilePath, AssetProvenance.PenumbraModDirectory)));
            var fromMods = await VerifyCandidatesAsync([installed], entry.Hash, entry.Extension, token).ConfigureAwait(false);
            if (fromMods.Match is { } modMatch)
            {
                Apply(entry, modMatch, entry.Hash, ContentRepresentation.OriginalFromInstalledMod);
                entry.Note = Append(entry.Note, "The original file was found in one of your installed mods (identical SHA-1).");
                return;
            }

            outcome = outcome.Merge(fromMods);
        }

        // PlayerSync may have applied a server-generated compressed texture instead of the original
        // (PairHandler.TryCalculateModdedDictionary / ActiveCompressionRedirects).
        if (character.CompressionRedirects.TryGetValue(entry.Hash, out var altHash) && ContentHash.IsValid(altHash))
        {
            altHash = ContentHash.Normalize(altHash);
            var alt = await VerifyCandidatesAsync(_locators, altHash, entry.Extension, token).ConfigureAwait(false);
            if (alt.Match is { } altMatch)
            {
                Apply(entry, altMatch, altHash, ContentRepresentation.CompressedAlternate);
                entry.Note = Append(entry.Note,
                    "Only PlayerSync's automatically compressed version of this texture is available locally. It is a different, re-encoded file, "
                    + "not the creator's original, and the original cannot be reconstructed from it.");
                return;
            }

            outcome = outcome.Merge(alt);
        }

        if (outcome.MismatchCount > 0)
        {
            entry.Status = AssetStatus.HashMismatch;
            entry.Note = Append(entry.Note, $"{outcome.MismatchCount} local cop{(outcome.MismatchCount == 1 ? "y" : "ies")} found, but the content does not match the expected hash (corrupt or modified).");
        }
        else if (outcome.Errors.Count > 0)
        {
            entry.Status = AssetStatus.Unreadable;
            entry.Note = Append(entry.Note, "A local copy exists but could not be read: " + outcome.Errors[0]);
        }
        else
        {
            entry.Status = AssetStatus.Missing;
            entry.Note = Append(entry.Note, "No local copy in the PlayerSync cache. It may not have finished downloading or was evicted.");
        }
    }

    private static void Apply(AssetEntry entry, (LocatedCandidate Candidate, long Size) match, string contentHash, ContentRepresentation representation)
    {
        entry.Status = AssetStatus.Available;
        entry.SourcePath = match.Candidate.Path;
        entry.Provenance = match.Candidate.Provenance;
        entry.Size = match.Size;
        entry.ContentHash = contentHash;
        entry.Representation = representation;
        entry.IsOriginalContent = representation != ContentRepresentation.CompressedAlternate;
    }

    private sealed record VerifyOutcome((LocatedCandidate Candidate, long Size)? Match, int MismatchCount, List<string> Errors)
    {
        public VerifyOutcome Merge(VerifyOutcome other)
            => new(Match ?? other.Match, MismatchCount + other.MismatchCount, [.. Errors, .. other.Errors]);
    }

    private async Task<VerifyOutcome> VerifyCandidatesAsync(IReadOnlyList<IAssetLocator> locators, string hash, string extension, CancellationToken token)
    {
        var mismatches = 0;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var locator in locators)
        {
            IEnumerable<LocatedCandidate> candidates;
            try
            {
                candidates = locator.Locate(hash, extension).ToList();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors.Add($"{locator.Name} failed: {e.Message}");
                continue;
            }

            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                string full;
                try
                {
                    full = Path.GetFullPath(candidate.Path);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (!seen.Add(full))
                    continue;

                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists)
                        continue;
                    if (info.LinkTarget != null)
                    {
                        errors.Add($"{full} is a link and was not followed.");
                        continue;
                    }

                    var actual = await _hashCache.GetOrComputeAsync(info, token).ConfigureAwait(false);
                    if (string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                        return new VerifyOutcome((candidate with { Path = full }, info.Length), mismatches, errors);
                    mismatches++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add(e.Message);
                }
            }
        }

        return new VerifyOutcome(null, mismatches, errors);
    }

    private static List<AssetGamePath> NormalizeGamePaths(IEnumerable<CapturedFileReplacement> files, ConcurrentBag<string> warnings)
    {
        var result = new List<AssetGamePath>();
        var seen = new HashSet<(SyncObjectKind, string)>();
        foreach (var file in files)
        foreach (var raw in file.GamePaths)
        {
            if (!PathSafety.TryNormalizeGamePath(raw, out var path))
            {
                warnings.Add($"Ignored invalid game path '{Truncate(raw)}' for {file.Hash}.");
                continue;
            }

            if (seen.Add((file.Kind, path)))
                result.Add(new AssetGamePath(file.Kind, path));
        }

        return result.OrderBy(p => p.Kind).ThenBy(p => p.GamePath, StringComparer.Ordinal).ToList();
    }

    private static List<ValidFileSwap> NormalizeSwaps(IEnumerable<CapturedFileSwap> swaps, ConcurrentBag<string> warnings)
    {
        var result = new Dictionary<(SyncObjectKind, string), ValidFileSwap>();
        foreach (var swap in swaps)
        {
            if (!PathSafety.TryNormalizeGamePath(swap.SwapTarget, out var target))
            {
                warnings.Add($"Ignored file swap with invalid target '{Truncate(swap.SwapTarget)}'.");
                continue;
            }

            foreach (var raw in swap.GamePaths)
            {
                if (PathSafety.TryNormalizeGamePath(raw, out var path))
                    result[(swap.Kind, path)] = new ValidFileSwap(swap.Kind, path, target);
                else
                    warnings.Add($"Ignored file swap with invalid game path '{Truncate(raw)}'.");
            }
        }

        return result.Values.OrderBy(s => s.Kind).ThenBy(s => s.GamePath, StringComparer.Ordinal).ToList();
    }

    private static string Truncate(string? s) => s is null ? "" : s.Length <= 120 ? s : s[..120] + "...";

    private static string Append(string? existing, string add) => string.IsNullOrEmpty(existing) ? add : existing + " " + add;
}
