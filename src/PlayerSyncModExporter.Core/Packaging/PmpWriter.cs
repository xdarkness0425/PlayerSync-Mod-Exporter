using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlayerSyncModExporter.Core.Packaging;

/// <summary>
/// Writes a Penumbra mod pack (.pmp): a zip archive with meta.json and default_mod.json at the root,
/// using Penumbra's FileVersion 3 layout. Penumbra (Import/TexToolsImporter.Archives.cs) extracts any zip
/// with a root meta.json; current Penumbra (Files/ModDeserialization.cs, MigrateGroups) reads
/// default_mod.json for FileVersion &lt; 4 and migrates it, and older Penumbra versions read it natively.
/// </summary>
public static class PmpWriter
{
    public const int PenumbraFileVersion = 3;
    public const string MetaEntry = "meta.json";
    public const string DefaultModEntry = "default_mod.json";
    public const string ManifestEntry = "export_manifest.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public sealed record PmpMetadata(string Name, string Author, string Description, string Version, IReadOnlyList<string> Tags);

    public static async Task WriteAsync(Stream output, PackagePlan plan, PmpMetadata metadata, string? manipulationsJson,
        Func<PlannedFile, Stream, CancellationToken, Task> writePayload, Func<string> manifestJson, CancellationToken token)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);

        await WriteTextAsync(archive, MetaEntry, BuildMetaJson(metadata), token).ConfigureAwait(false);
        await WriteTextAsync(archive, DefaultModEntry, BuildDefaultModJson(plan, manipulationsJson), token).ConfigureAwait(false);

        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            // Textures and models are already dense; Fastest keeps CPU time low with little size cost.
            var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Fastest);
            await using var stream = entry.Open();
            await writePayload(file, stream, token).ConfigureAwait(false);
        }

        // Written last so it reflects the final, verified plan.
        await WriteTextAsync(archive, ManifestEntry, manifestJson(), token).ConfigureAwait(false);
    }

    public static string BuildMetaJson(PmpMetadata metadata)
    {
        var meta = new JsonObject
        {
            ["FileVersion"] = PenumbraFileVersion,
            ["Name"] = StripControl(metadata.Name),
            ["Author"] = StripControl(metadata.Author),
            ["Description"] = metadata.Description,
            ["Version"] = metadata.Version,
            ["Website"] = string.Empty,
            ["ModTags"] = new JsonArray(metadata.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
        };
        return meta.ToJsonString(Indented);
    }

    public static string BuildDefaultModJson(PackagePlan plan, string? manipulationsJson)
    {
        var files = new JsonObject();
        foreach (var (gamePath, archivePath) in plan.Redirections)
            files[gamePath] = archivePath.Replace('/', '\\'); // Penumbra stores relative paths with backslashes.

        var swaps = new JsonObject();
        foreach (var swap in plan.FileSwaps)
            swaps[swap.GamePath] = swap.Target;

        var manipulations = string.IsNullOrWhiteSpace(manipulationsJson)
            ? new JsonArray()
            : JsonNode.Parse(manipulationsJson) as JsonArray ?? throw new InvalidDataException("Manipulations must be a JSON array.");

        var mod = new JsonObject
        {
            ["Name"] = string.Empty,
            ["Description"] = string.Empty,
            ["Files"] = files,
            ["FileSwaps"] = swaps,
            ["Manipulations"] = manipulations,
        };
        return mod.ToJsonString(Indented);
    }

    private static async Task WriteTextAsync(ZipArchive archive, string name, string content, CancellationToken token)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static string StripControl(string s)
        => new(s.Where(c => !char.IsControl(c) && c != (char)0x2028 && c != (char)0x2029).ToArray());
}
