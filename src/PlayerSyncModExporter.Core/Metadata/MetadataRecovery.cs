using PlayerSyncModExporter.Core.Hashing;
using PlayerSyncModExporter.Core.Model;

namespace PlayerSyncModExporter.Core.Metadata;

public enum MetadataClassification
{
    /// <summary>Read from authoritative data: an installed mod whose file for this game path has the exact same SHA-1.</summary>
    Recovered,

    /// <summary>Derived from structural evidence (scores, game path layout) and labeled as such.</summary>
    Inferred,

    /// <summary>Not present in any accessible data.</summary>
    Unavailable,
}

/// <summary>Where one exported game path came from. Mod is null when unattributed.</summary>
public sealed class PathAttribution
{
    public required string GamePath { get; init; }
    public required MetadataClassification Classification { get; init; }
    public LocalMod? Mod { get; init; }

    /// <summary>Null for the mod's default container.</summary>
    public LocalModGroup? Group { get; init; }

    /// <summary>Options of <see cref="Group"/> the file is placed in (empty for the default container).</summary>
    public IReadOnlyList<LocalModOption> Options { get; init; } = [];

    /// <summary>Folder names of every installed mod with a byte-identical file for this game path.</summary>
    public IReadOnlyList<string> CandidateMods { get; init; } = [];

    public string? Note { get; init; }
}

/// <summary>A group of an installed mod as it is reproduced in the export.</summary>
public sealed class RecoveredGroup
{
    public required LocalModGroup Group { get; init; }

    /// <summary>Options the character uses, in the original order.</summary>
    public required IReadOnlyList<LocalModOption> UsedOptions { get; init; }
}

public sealed class RecoveredSourceMod
{
    public required LocalMod Mod { get; init; }
    public required int MatchedPaths { get; init; }
    public required IReadOnlyList<RecoveredGroup> Groups { get; init; }
}

public sealed class MetadataRecoveryResult
{
    public static MetadataRecoveryResult None { get; } = new() { LibraryAvailable = false };

    /// <summary>False when no Penumbra mod directory could be read; then nothing can be recovered.</summary>
    public required bool LibraryAvailable { get; init; }
    public int ModsIndexed { get; init; }
    public IReadOnlyList<string> LibraryProblems { get; init; } = [];
    public IReadOnlyList<RecoveredSourceMod> SourceMods { get; init; } = [];

    /// <summary>Keyed by (expected asset hash, game path).</summary>
    public IReadOnlyDictionary<(string Hash, string GamePath), PathAttribution> Paths { get; init; }
        = new Dictionary<(string, string), PathAttribution>();

    public PathAttribution? Find(string hash, string gamePath)
        => Paths.GetValueOrDefault((hash.ToUpperInvariant(), gamePath));
}

/// <summary>
/// Attributes captured files to installed mods. A file is only attributed when an installed mod option redirects the same
/// game path to a file whose SHA-1 equals the hash PlayerSync referenced, so names come from the mod's own metadata,
/// never from guesses. Which options the character has enabled is inferred from the matches and labeled as inferred.
/// </summary>
public static class MetadataRecoverer
{
    public static async Task<MetadataRecoveryResult> RecoverAsync(IReadOnlyList<AssetEntry> assets, ModLibraryIndex library,
        FileHashCache hashCache, CancellationToken token = default)
    {
        if (library.Mods.Count == 0)
            return new MetadataRecoveryResult
            {
                LibraryAvailable = !string.IsNullOrEmpty(library.Root), LibraryProblems = library.Problems,
            };

        // 1. Exact matches: (hash, game path) -> installed options that redirect that game path to identical bytes.
        var matches = new Dictionary<(string Hash, string GamePath), List<ModOptionRef>>();
        foreach (var asset in assets.Where(a => a.Status == AssetStatus.Available))
        foreach (var gamePath in asset.GamePaths.Select(p => p.GamePath).Distinct(StringComparer.Ordinal))
        {
            var found = new List<ModOptionRef>();
            foreach (var candidate in library.Lookup(gamePath))
            {
                token.ThrowIfCancellationRequested();
                if (await HashMatchesAsync(candidate.FilePath, asset, hashCache, token).ConfigureAwait(false))
                    found.Add(candidate);
            }

            if (found.Count > 0)
                matches[(asset.Hash, gamePath)] = found;
        }

        // 2. Choose a mod per path. Unique -> Recovered; several -> the mod with strictly most matches overall (Inferred); tie -> none.
        var modScore = matches.Values.SelectMany(m => m.Select(r => r.Mod).Distinct())
            .GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count());
        var chosen = new Dictionary<(string, string), (LocalMod? Mod, MetadataClassification Class, List<ModOptionRef> Refs, string? Note)>();
        foreach (var (key, refs) in matches)
        {
            var mods = refs.Select(r => r.Mod).Distinct().ToList();
            if (mods.Count == 1)
            {
                chosen[key] = (mods[0], MetadataClassification.Recovered, refs, null);
                continue;
            }

            var ranked = mods.OrderByDescending(m => modScore[m]).ToList();
            if (modScore[ranked[0]] > modScore[ranked[1]])
                chosen[key] = (ranked[0], MetadataClassification.Inferred, refs.Where(r => r.Mod == ranked[0]).ToList(),
                    $"Identical files exist in {mods.Count} installed mods; attributed to the one that matches the most files of this character.");
            else
                chosen[key] = (null, MetadataClassification.Unavailable, refs,
                    $"Identical files exist in {mods.Count} installed mods that match equally well; the source cannot be determined.");
        }

        // 3. Inferred selection for single-choice groups: the option with the most matched paths (lowest index on ties).
        var singleChoice = new Dictionary<LocalModGroup, LocalModOption>();
        foreach (var group in chosen.Values.Where(c => c.Mod != null).SelectMany(c => c.Refs)
                     .Where(r => r.Group is { Type: LocalGroupType.Single })
                     .GroupBy(r => r.Group!))
        {
            singleChoice[group.Key] = group.GroupBy(r => r.Option)
                .OrderByDescending(o => o.Count()).ThenBy(o => o.Key.Index).First().Key;
        }

        // 4. Place every path in exactly one container of the chosen mod.
        var paths = new Dictionary<(string, string), PathAttribution>();
        foreach (var asset in assets)
        foreach (var gamePath in asset.GamePaths.Select(p => p.GamePath).Distinct(StringComparer.Ordinal))
        {
            var key = (asset.Hash, gamePath);
            if (!chosen.TryGetValue(key, out var c))
                continue;

            var candidates = matches[key].Select(r => r.Mod.FolderName).Distinct().Order(StringComparer.Ordinal).ToList();
            if (c.Mod == null)
            {
                paths[key] = new PathAttribution
                {
                    GamePath = gamePath, Classification = c.Class, CandidateMods = candidates, Note = c.Note,
                };
                continue;
            }

            paths[key] = Place(gamePath, c.Mod, c.Class, c.Refs, singleChoice, candidates, c.Note);
        }

        var sourceMods = paths.Values.Where(p => p.Mod != null).GroupBy(p => p.Mod!)
            .Select(g => new RecoveredSourceMod
            {
                Mod = g.Key,
                MatchedPaths = g.Count(),
                Groups = g.Where(p => p.Group != null).GroupBy(p => p.Group!)
                    .OrderBy(x => x.Key.Index)
                    .Select(x => new RecoveredGroup
                    {
                        Group = x.Key,
                        UsedOptions = x.SelectMany(p => p.Options).Distinct().OrderBy(o => o.Index).ToList(),
                    }).ToList(),
            })
            .OrderBy(m => m.Mod.DisplayName, StringComparer.Ordinal).ThenBy(m => m.Mod.FolderName, StringComparer.Ordinal)
            .ToList();

        return new MetadataRecoveryResult
        {
            LibraryAvailable = true,
            ModsIndexed = library.Mods.Count,
            LibraryProblems = library.Problems,
            SourceMods = sourceMods,
            Paths = paths,
        };
    }

    private static PathAttribution Place(string gamePath, LocalMod mod, MetadataClassification classification, List<ModOptionRef> refs,
        Dictionary<LocalModGroup, LocalModOption> singleChoice, IReadOnlyList<string> candidates, string? note)
    {
        // The mod's default container is always applied, so a file it contains needs no option.
        if (refs.Any(r => r.Group == null))
            return new PathAttribution { GamePath = gamePath, Classification = classification, Mod = mod, CandidateMods = candidates, Note = note };

        foreach (var group in refs.Select(r => r.Group!).Distinct().OrderBy(g => g.Index))
        {
            var options = refs.Where(r => r.Group == group).Select(r => r.Option).Distinct().OrderBy(o => o.Index).ToList();
            if (group.Type == LocalGroupType.Multi)
                return new PathAttribution
                {
                    GamePath = gamePath, Classification = classification, Mod = mod, Group = group, Options = options,
                    CandidateMods = candidates, Note = note,
                };

            if (singleChoice.TryGetValue(group, out var selected) && options.Contains(selected))
                return new PathAttribution
                {
                    GamePath = gamePath, Classification = classification, Mod = mod, Group = group, Options = [selected],
                    CandidateMods = candidates, Note = note,
                };
        }

        // Only matches options of single-choice groups that the rest of the character does not use.
        return new PathAttribution
        {
            GamePath = gamePath, Classification = classification, Mod = mod, CandidateMods = candidates,
            Note = (note == null ? "" : note + " ")
                   + "The matching option is not the one this character appears to use in that group, so the file is kept in the package's default option.",
        };
    }

    private static async Task<bool> HashMatchesAsync(string path, AssetEntry asset, FileHashCache hashCache, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget != null)
                return false;
            // A file with the original content has the same size as the verified local copy.
            if (asset.IsOriginalContent && asset.Size > 0 && info.Length != asset.Size)
                return false;
            var hash = await hashCache.GetOrComputeAsync(info, token).ConfigureAwait(false);
            return string.Equals(hash, asset.Hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
