namespace PlayerSyncModExporter.Core.Metadata;

/// <summary>Evidence-only catalog view of installed mods with exact (SHA-1, game path) matches.</summary>
public sealed record ModFingerprintMatch(
    string ModFolder,
    string ModName,
    int MatchedPaths,
    int AmbiguousPaths,
    IReadOnlyList<string> OptionNames);

public static class ModFingerprintCatalog
{
    /// <summary>
    /// Uses the verified candidates retained by MetadataRecoverer. Counts distinct observed game paths;
    /// no source mod is asserted as the actual package installed by the selected player.
    /// </summary>
    public static IReadOnlyList<ModFingerprintMatch> From(MetadataRecoveryResult recovery)
    {
        var byFolder = new Dictionary<string, (string Name, HashSet<string> Paths, HashSet<string> Ambiguous, HashSet<string> Options)>(
            StringComparer.Ordinal);

        foreach (var ((_, gamePath), attribution) in recovery.Paths)
        {
            foreach (var folder in attribution.CandidateMods.Distinct(StringComparer.Ordinal))
            {
                if (!byFolder.TryGetValue(folder, out var entry))
                    entry = (folder, new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                entry.Paths.Add(gamePath);
                if (attribution.CandidateMods.Count > 1)
                    entry.Ambiguous.Add(gamePath);
                if (attribution.Mod?.FolderName == folder && attribution.Group != null)
                    foreach (var option in attribution.Options)
                        entry.Options.Add(attribution.Group.Name + " / " + option.Name);
                byFolder[folder] = entry;
            }
        }

        var names = recovery.SourceMods.ToDictionary(s => s.Mod.FolderName, s => s.Mod.DisplayName, StringComparer.Ordinal);
        return byFolder.OrderBy(kv => names.GetValueOrDefault(kv.Key, kv.Key), StringComparer.Ordinal)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ModFingerprintMatch(
                kv.Key, names.GetValueOrDefault(kv.Key, kv.Key),
                kv.Value.Paths.Count, kv.Value.Ambiguous.Count,
                kv.Value.Options.Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
