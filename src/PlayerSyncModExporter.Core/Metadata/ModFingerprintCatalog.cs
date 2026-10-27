namespace PlayerSyncModExporter.Core.Metadata;

/// <summary>
/// One verified installed-mod candidate for the exact (original SHA-1, game path) observed
/// on a permitted, selected pair. More than one mod or option may match the same fingerprint.
/// This does not prove the sender installed any particular package or enabled an option.
/// </summary>
public sealed record ModFingerprintEvidence(
    string Hash,
    string GamePath,
    string ModFolder,
    string ModName,
    int GroupIndex,
    string? GroupName,
    int OptionIndex,
    string? OptionName);

public enum FingerprintResourceKind
{
    Model,
    Material,
    Texture,
    Animation,
    Effect,
    Audio,
    Other,
}

/// <summary>One matching resource with the number of known packages containing its fingerprint.</summary>
public sealed record FingerprintPathMatch(
    string Hash,
    string GamePath,
    FingerprintResourceKind Kind,
    int CatalogModCount);

/// <summary>Matching files for an option defined by the user's local Penumbra metadata.</summary>
public sealed record FingerprintOptionMatch(
    string GroupName,
    string OptionName,
    bool IsDefault,
    int MatchedPaths,
    int SharedPaths);

/// <summary>Evidence-based catalog match, not identification of the sender's installed package.</summary>
public sealed record ModFingerprintMatch(
    string ModFolder,
    string ModName,
    int MatchedPaths,
    int AmbiguousPaths,
    int DistinctivePaths,
    IReadOnlyList<FingerprintOptionMatch> OptionMatches,
    IReadOnlyList<FingerprintPathMatch> Files)
{
    public IReadOnlyList<string> OptionNames =>
        OptionMatches.Where(o => !o.IsDefault)
            .Select(o => o.GroupName + " / " + o.OptionName)
            .ToList();
}

/// <summary>
/// Builds an evidence-only candidate catalog from SHA-1 and game-path matches verified
/// against the user's own installed mods. Never infers which remote options are active.
/// </summary>
public static class ModFingerprintCatalog
{
    public static IReadOnlyList<ModFingerprintMatch> From(MetadataRecoveryResult recovery)
    {
        // One mod may put the same resource in several options: it counts only once
        // toward how widespread the fingerprint is among installed mod packages.
        var evidence = recovery.FingerprintEvidence
            .Distinct()
            .ToList();

        var popularity = evidence
            .GroupBy(e => (e.Hash, e.GamePath))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ModFolder).Distinct(StringComparer.Ordinal).Count());

        var result = new List<ModFingerprintMatch>();
        foreach (var mod in evidence.GroupBy(e => e.ModFolder, StringComparer.Ordinal))
        {
            var fileMatches = mod.GroupBy(e => (e.Hash, e.GamePath))
                .Select(g => new FingerprintPathMatch(
                    g.Key.Hash, g.Key.GamePath, Classify(g.Key.GamePath), popularity[g.Key]))
                .OrderBy(f => f.GamePath, StringComparer.Ordinal)
                .ThenBy(f => f.Hash, StringComparer.Ordinal)
                .ToList();

            var uniquePaths = fileMatches.Select(f => f.GamePath).Distinct(StringComparer.Ordinal).ToList();
            var sharedPaths = fileMatches.Where(f => f.CatalogModCount > 1)
                .Select(f => f.GamePath).Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var optionMatches = mod.GroupBy(e => (e.GroupIndex, e.OptionIndex))
                .Select(group =>
                {
                    var first = group.First();
                    return new
                    {
                        first.GroupIndex,
                        first.OptionIndex,
                        Match = new FingerprintOptionMatch(
                            first.GroupName ?? "Default",
                            first.OptionName ?? "Default",
                            first.GroupIndex < 0,
                            group.Select(e => e.GamePath).Distinct(StringComparer.Ordinal).Count(),
                            group.Select(e => e.GamePath).Distinct(StringComparer.Ordinal)
                                .Count(p => sharedPaths.Contains(p))),
                    };
                })
                .OrderBy(o => o.GroupIndex)
                .ThenBy(o => o.OptionIndex)
                .Select(o => o.Match)
                .ToList();

            result.Add(new ModFingerprintMatch(
                mod.Key,
                mod.First().ModName,
                uniquePaths.Count,
                sharedPaths.Count,
                uniquePaths.Count - sharedPaths.Count,
                optionMatches,
                fileMatches));
        }

        // Prioritize evidence that is distinctive within the *local* catalog,
        // without presenting it as a probability of the sender using that mod.
        return result.OrderByDescending(m => m.DistinctivePaths)
            .ThenByDescending(m => m.MatchedPaths)
            .ThenBy(m => m.ModName, StringComparer.Ordinal)
            .ThenBy(m => m.ModFolder, StringComparer.Ordinal)
            .ToList();
    }

    public static FingerprintResourceKind Classify(string gamePath)
    {
        var dot = gamePath.LastIndexOf('.');
        var ext = dot < 0 ? string.Empty : gamePath[(dot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "mdl" => FingerprintResourceKind.Model,
            "mtrl" => FingerprintResourceKind.Material,
            "tex" => FingerprintResourceKind.Texture,
            "pap" or "tmb" or "sklb" or "skp" => FingerprintResourceKind.Animation,
            "avfx" or "atex" => FingerprintResourceKind.Effect,
            "scd" => FingerprintResourceKind.Audio,
            _ => FingerprintResourceKind.Other,
        };
    }
}
