using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Metadata;
using PlayerSyncModExporter.Core.Model;

namespace PlayerSyncModExporter.Core.Packaging;

/// <summary>One unique payload written to the package.</summary>
public sealed record PlannedFile(string ContentHash, string SourcePath, long Size, string ArchivePath, IReadOnlyList<AssetEntry> Assets);

/// <summary>One option of a reproduced group: game path -> archive path (forward slashes).</summary>
public sealed record PlannedOption(LocalModOption Source, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// An option group of an installed mod, reproduced with the options this character uses. Name is unique within the package.
/// </summary>
public sealed record PlannedGroup(string Name, RecoveredSourceMod SourceMod, LocalModGroup Source, IReadOnlyList<PlannedOption> Options)
{
    public bool IsMulti => Source.Type == LocalGroupType.Multi;

    /// <summary>Single: index of the selected option. Multi: bit mask with every reproduced option enabled.</summary>
    public ulong DefaultSettings => IsMulti ? (Options.Count >= 64 ? ulong.MaxValue : (1UL << Options.Count) - 1) : 0UL;
}

/// <summary>
/// The exact set of payloads and redirections an export will contain, computed before anything is written
/// so the preview, disk-space check, writers and manifest all agree.
/// </summary>
public sealed class PackagePlan
{
    public required IReadOnlyList<PlannedFile> Files { get; init; }

    /// <summary>Every game path -> archive path (forward slashes) in the package, wherever it is placed.</summary>
    public required IReadOnlyDictionary<string, string> Redirections { get; init; }

    /// <summary>The subset of <see cref="Redirections"/> written to the PMP default option.</summary>
    public required IReadOnlyDictionary<string, string> DefaultRedirections { get; init; }

    /// <summary>Option groups recovered from installed mods (empty when nothing could be attributed).</summary>
    public IReadOnlyList<PlannedGroup> Groups { get; init; } = [];

    public required IReadOnlyList<ValidFileSwap> FileSwaps { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public long TotalBytes => Files.Sum(f => f.Size);

    public static PackagePlan Create(AnalysisResult analysis, bool includeNonPlayerObjects)
    {
        var warnings = new List<string>();
        var included = analysis.Assets.Where(a => a.Include && a.IsExportable).ToList();

        // Deduplicate by the bytes actually exported: two references with identical content share one archive entry.
        var files = included
            .GroupBy(a => a.ContentHash!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var archivePath = $"files/{g.Key.ToLowerInvariant()}.{(string.IsNullOrEmpty(first.Extension) ? "bin" : first.Extension)}";
                return new PlannedFile(g.Key.ToUpperInvariant(), first.SourcePath!, first.Size, archivePath, g.ToList());
            })
            .OrderBy(f => f.ArchivePath, StringComparer.Ordinal)
            .ToList();

        var byContent = files.SelectMany(f => f.Assets.Select(a => (a, f))).ToDictionary(x => x.a, x => x.f);
        var redirections = new Dictionary<string, (string Archive, SyncObjectKind Kind, string Hash, string AssetHash)>(StringComparer.Ordinal);
        foreach (var asset in included)
        foreach (var gp in asset.GamePaths)
        {
            if (gp.Kind != SyncObjectKind.Player && !includeNonPlayerObjects)
                continue;

            var planned = byContent[asset];
            if (redirections.TryGetValue(gp.GamePath, out var existing))
            {
                if (!string.Equals(existing.Hash, planned.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Player data wins over minion/pet data; otherwise first one wins.
                    var replace = gp.Kind < existing.Kind;
                    warnings.Add($"Game path '{gp.GamePath}' is replaced by different files for {existing.Kind} and {gp.Kind}; kept the {(replace ? gp.Kind : existing.Kind)} version.");
                    if (replace)
                        redirections[gp.GamePath] = (planned.ArchivePath, gp.Kind, planned.ContentHash, asset.Hash);
                }

                continue;
            }

            redirections[gp.GamePath] = (planned.ArchivePath, gp.Kind, planned.ContentHash, asset.Hash);
        }

        var swaps = new List<ValidFileSwap>();
        foreach (var swap in analysis.FileSwaps)
        {
            if (swap.Kind != SyncObjectKind.Player && !includeNonPlayerObjects)
                continue;
            if (redirections.ContainsKey(swap.GamePath))
            {
                warnings.Add($"File swap for '{swap.GamePath}' was dropped because a file replacement already targets that path.");
                continue;
            }

            if (swaps.Any(s => s.GamePath == swap.GamePath))
                continue;
            swaps.Add(swap);
        }

        // Only keep payloads that are still referenced after conflict resolution.
        var referenced = redirections.Values.Select(v => v.Archive).ToHashSet(StringComparer.Ordinal);
        var unreferenced = files.Where(f => !referenced.Contains(f.ArchivePath)).ToList();
        foreach (var f in unreferenced)
            warnings.Add($"{f.ArchivePath} is not referenced by any included game path and was left out.");

        var (groups, defaults) = PlaceInGroups(redirections.ToDictionary(kv => kv.Key, kv => (kv.Value.Archive, kv.Value.AssetHash), StringComparer.Ordinal),
            analysis.Metadata, warnings);

        return new PackagePlan
        {
            Files = files.Where(f => referenced.Contains(f.ArchivePath)).ToList(),
            Redirections = redirections.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value.Archive, StringComparer.Ordinal),
            DefaultRedirections = defaults,
            Groups = groups,
            FileSwaps = swaps,
            Warnings = warnings,
        };
    }

    private const int MaxMultiOptions = 32; // Penumbra IModGroup.MaxMultiOptions

    /// <summary>
    /// Moves game paths attributed to an option of an installed mod into a group that reproduces it. Everything else,
    /// including files from a mod's default container, stays in the PMP default option.
    /// </summary>
    private static (List<PlannedGroup> Groups, Dictionary<string, string> Defaults) PlaceInGroups(
        Dictionary<string, (string Archive, string AssetHash)> redirections, MetadataRecoveryResult metadata, List<string> warnings)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        var byGroup = new Dictionary<LocalModGroup, Dictionary<LocalModOption, Dictionary<string, string>>>();
        foreach (var (gamePath, (archive, assetHash)) in redirections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var attribution = metadata.Find(assetHash, gamePath);
            if (attribution?.Group == null || attribution.Options.Count == 0)
            {
                defaults[gamePath] = archive;
                continue;
            }

            if (!byGroup.TryGetValue(attribution.Group, out var options))
                byGroup[attribution.Group] = options = [];
            foreach (var option in attribution.Options)
            {
                if (!options.TryGetValue(option, out var files))
                    options[option] = files = new Dictionary<string, string>(StringComparer.Ordinal);
                files[gamePath] = archive;
            }
        }

        var groups = new List<PlannedGroup>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in metadata.SourceMods)
        foreach (var recovered in source.Groups)
        {
            if (!byGroup.TryGetValue(recovered.Group, out var options))
                continue;

            var ordered = options.OrderBy(o => o.Key.Index).Select(o => new PlannedOption(o.Key, o.Value)).ToList();
            if (recovered.Group.Type == LocalGroupType.Multi && ordered.Count > MaxMultiOptions)
            {
                foreach (var extra in ordered.Skip(MaxMultiOptions))
                foreach (var (gamePath, archive) in extra.Files)
                    defaults.TryAdd(gamePath, archive);
                warnings.Add($"'{source.Mod.DisplayName}' group '{recovered.Group.Name}' uses more than {MaxMultiOptions} options; the rest were placed in the default option.");
                ordered = ordered.Take(MaxMultiOptions).ToList();
            }

            var baseName = $"{source.Mod.DisplayName} - {(string.IsNullOrWhiteSpace(recovered.Group.Name) ? "Group " + (recovered.Group.Index + 1) : recovered.Group.Name)}";
            baseName = baseName.Length > 120 ? baseName[..120] : baseName;
            var name = baseName;
            for (var i = 2; !usedNames.Add(name); i++)
                name = $"{baseName} ({i})";
            groups.Add(new PlannedGroup(name, source, recovered.Group, ordered));
        }

        // Files of a multi option may also belong to a group that was cut above; keep every game path somewhere.
        foreach (var (gamePath, (archive, _)) in redirections)
            if (!defaults.ContainsKey(gamePath) && !groups.Any(g => g.Options.Any(o => o.Files.ContainsKey(gamePath))))
                defaults[gamePath] = archive;

        return (groups, defaults);
    }
}
