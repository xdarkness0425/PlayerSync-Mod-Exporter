using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Model;

namespace PlayerSyncModExporter.Core.Packaging;

/// <summary>One unique payload written to the package.</summary>
public sealed record PlannedFile(string ContentHash, string SourcePath, long Size, string ArchivePath, IReadOnlyList<AssetEntry> Assets);

/// <summary>
/// The exact set of payloads and redirections an export will contain, computed before anything is written
/// so the preview, disk-space check, writers and manifest all agree.
/// </summary>
public sealed class PackagePlan
{
    public required IReadOnlyList<PlannedFile> Files { get; init; }

    /// <summary>Game path -> archive path (forward slashes) for the PMP default option.</summary>
    public required IReadOnlyDictionary<string, string> Redirections { get; init; }

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
        var redirections = new Dictionary<string, (string Archive, SyncObjectKind Kind, string Hash)>(StringComparer.Ordinal);
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
                        redirections[gp.GamePath] = (planned.ArchivePath, gp.Kind, planned.ContentHash);
                }

                continue;
            }

            redirections[gp.GamePath] = (planned.ArchivePath, gp.Kind, planned.ContentHash);
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

        return new PackagePlan
        {
            Files = files.Where(f => referenced.Contains(f.ArchivePath)).ToList(),
            Redirections = redirections.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value.Archive, StringComparer.Ordinal),
            FileSwaps = swaps,
            Warnings = warnings,
        };
    }
}
