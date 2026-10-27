using System.Text.Json;
using PlayerSyncModExporter.Core.IO;

namespace PlayerSyncModExporter.Core.Metadata;

public enum LocalGroupType
{
    Single,
    Multi,

    /// <summary>IMC, combining or unknown groups. Their names are read, but their files are not used for matching.</summary>
    Unsupported,
}

/// <summary>One option (or the default container, Index -1) of an installed mod. Files maps normalized game path to an absolute path.</summary>
public sealed record LocalModOption(int Index, string Name, string Description, int Priority, IReadOnlyDictionary<string, string> Files);

public sealed record LocalModGroup(
    int Index, string Name, string Description, string TypeName, LocalGroupType Type, int Priority, ulong DefaultSettings,
    IReadOnlyList<LocalModOption> Options);

/// <summary>An installed Penumbra mod as described by its own meta.json and group files.</summary>
public sealed class LocalMod
{
    public required string FolderName { get; init; }
    public required string Directory { get; init; }
    public int FileVersion { get; init; }
    /// <summary>Name exactly as in meta.json (may be empty).</summary>
    public required string Name { get; init; }

    /// <summary>Name for display; the folder name when meta.json has none.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FolderName : Name;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Website { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required LocalModOption Default { get; init; }
    public required IReadOnlyList<LocalModGroup> Groups { get; init; }
}

/// <summary>Where one game path is redirected inside an installed mod. Group is null for the default container.</summary>
public sealed record ModOptionRef(LocalMod Mod, LocalModGroup? Group, LocalModOption Option, string FilePath);

public sealed class ModLibraryLimits
{
    public long MaxJsonBytes { get; init; } = 16L * 1024 * 1024;
    public int MaxMods { get; init; } = 50_000;
    public int MaxGroupsPerMod { get; init; } = 512;
    public int MaxOptionsPerGroup { get; init; } = 512;
    public int MaxRedirectionsPerMod { get; init; } = 250_000;
}

/// <summary>
/// Read-only index of the local Penumbra mod directory: which installed mod option redirects which game path to which file.
/// This is the only place original mod names and option groups exist on the receiving side, because PlayerSync transfers
/// only content hashes and game paths (MareAPI FileReplacementData).
/// </summary>
public sealed class ModLibraryIndex
{
    private readonly Dictionary<string, List<ModOptionRef>> _byGamePath;

    private ModLibraryIndex(string root, IReadOnlyList<LocalMod> mods, IReadOnlyList<string> problems)
    {
        Root = root;
        Mods = mods;
        Problems = problems;
        _byGamePath = new Dictionary<string, List<ModOptionRef>>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            Add(mod, null, mod.Default);
            foreach (var group in mod.Groups.Where(g => g.Type != LocalGroupType.Unsupported))
            foreach (var option in group.Options)
                Add(mod, group, option);
        }
    }

    public static ModLibraryIndex Empty { get; } = new(string.Empty, [], []);

    public string Root { get; }
    public IReadOnlyList<LocalMod> Mods { get; }

    /// <summary>Mods that could not be read and why (malformed JSON, size limits, ...).</summary>
    public IReadOnlyList<string> Problems { get; }

    public IReadOnlyList<ModOptionRef> Lookup(string gamePath)
        => _byGamePath.TryGetValue(gamePath, out var list) ? list : [];

    public static Task<ModLibraryIndex> BuildAsync(string? root, ModLibraryLimits? limits = null, CancellationToken token = default)
        => Task.Run(() => Build(root, limits, token), token);

    public static ModLibraryIndex Build(string? root, ModLibraryLimits? limits = null, CancellationToken token = default)
    {
        limits ??= new ModLibraryLimits();
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root))
            return Empty;

        var mods = new List<LocalMod>();
        var problems = new List<string>();
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (mods.Count >= limits.MaxMods)
            {
                problems.Add($"Stopped after {limits.MaxMods} mods.");
                break;
            }

            if (dir.LinkTarget != null || !File.Exists(Path.Combine(dir.FullName, "meta.json")))
                continue;

            var mod = ModLibraryReader.TryRead(dir.FullName, limits, out var problem);
            if (mod != null)
                mods.Add(mod);
            if (problem != null)
                problems.Add($"{dir.Name}: {problem}");
        }

        return new ModLibraryIndex(Path.GetFullPath(root), mods, problems);
    }

    private void Add(LocalMod mod, LocalModGroup? group, LocalModOption option)
    {
        foreach (var (gamePath, file) in option.Files)
        {
            if (!_byGamePath.TryGetValue(gamePath, out var list))
                _byGamePath[gamePath] = list = [];
            list.Add(new ModOptionRef(mod, group, option, file));
        }
    }
}

/// <summary>
/// Reads Penumbra's mod description files. Supports FileVersion 3 (meta.json + default_mod.json + group_NNN_*.json)
/// and FileVersion 4 (meta.json with DefaultData and Groups), as written by Penumbra's ModSerialization/GroupSerialization.
/// </summary>
public static class ModLibraryReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64,
    };

    public static LocalMod? TryRead(string modDirectory, ModLibraryLimits? limits, out string? problem)
    {
        limits ??= new ModLibraryLimits();
        problem = null;
        var budget = new Budget(limits.MaxRedirectionsPerMod);
        try
        {
            using var meta = Parse(Path.Combine(modDirectory, "meta.json"), limits);
            var root = meta.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("meta.json is not an object.");

            var fileVersion = root.TryGetProperty("FileVersion", out var fv) && fv.TryGetInt32(out var v) ? v : 0;
            LocalModOption defaultOption;
            var groups = new List<LocalModGroup>();
            if (root.TryGetProperty("DefaultData", out _) || root.TryGetProperty("Groups", out _) || fileVersion >= 4)
            {
                defaultOption = root.TryGetProperty("DefaultData", out var data) && data.ValueKind == JsonValueKind.Object
                    ? ReadOption(data, -1, modDirectory, budget, isDefault: true)
                    : EmptyDefault;
                if (root.TryGetProperty("Groups", out var groupArray) && groupArray.ValueKind == JsonValueKind.Array)
                    foreach (var g in groupArray.EnumerateArray())
                    {
                        if (groups.Count >= limits.MaxGroupsPerMod)
                            throw new InvalidDataException($"more than {limits.MaxGroupsPerMod} option groups.");
                        if (g.ValueKind == JsonValueKind.Object)
                            groups.Add(ReadGroup(g, groups.Count, modDirectory, limits, budget));
                    }
            }
            else
            {
                var defaultFile = Path.Combine(modDirectory, "default_mod.json");
                if (File.Exists(defaultFile))
                {
                    using var def = Parse(defaultFile, limits);
                    defaultOption = def.RootElement.ValueKind == JsonValueKind.Object
                        ? ReadOption(def.RootElement, -1, modDirectory, budget, isDefault: true)
                        : EmptyDefault;
                }
                else
                {
                    defaultOption = EmptyDefault;
                }

                // Penumbra numbers group files group_001_..., so ordinal order is group order.
                foreach (var file in System.IO.Directory.EnumerateFiles(modDirectory, "group_*.json").Order(StringComparer.Ordinal))
                {
                    if (groups.Count >= limits.MaxGroupsPerMod)
                        throw new InvalidDataException($"more than {limits.MaxGroupsPerMod} option groups.");
                    using var doc = Parse(file, limits);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        groups.Add(ReadGroup(doc.RootElement, groups.Count, modDirectory, limits, budget));
                }
            }

            var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(modDirectory));
            return new LocalMod
            {
                FolderName = folder,
                Directory = Path.GetFullPath(modDirectory),
                FileVersion = fileVersion,
                Name = Str(root, "Name"),
                Author = Str(root, "Author"),
                Description = Str(root, "Description"),
                Version = Str(root, "Version"),
                Website = Str(root, "Website"),
                Tags = root.TryGetProperty("ModTags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                    : [],
                Default = defaultOption,
                Groups = groups,
            };
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException
                                      or InvalidOperationException or FormatException)
        {
            problem = e.Message;
            return null;
        }
    }

    private static readonly LocalModOption EmptyDefault = new(-1, "Default", string.Empty, 0, new Dictionary<string, string>());

    private static LocalModGroup ReadGroup(JsonElement g, int index, string modDirectory, ModLibraryLimits limits, Budget budget)
    {
        // Penumbra treats a group without "Type" as Single (GroupDeserialization.ReadGroup).
        var typeName = Str(g, "Type");
        var type = typeName switch
        {
            "" or "Single" => LocalGroupType.Single,
            "Multi" => LocalGroupType.Multi,
            _ => LocalGroupType.Unsupported,
        };
        var options = new List<LocalModOption>();
        if (g.TryGetProperty("Options", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var o in array.EnumerateArray())
            {
                if (options.Count >= limits.MaxOptionsPerGroup)
                    throw new InvalidDataException($"more than {limits.MaxOptionsPerGroup} options in a group.");
                if (o.ValueKind != JsonValueKind.Object)
                    continue;
                options.Add(type == LocalGroupType.Unsupported
                    ? new LocalModOption(options.Count, Str(o, "Name"), Str(o, "Description"), 0, new Dictionary<string, string>())
                    : ReadOption(o, options.Count, modDirectory, budget, isDefault: false));
            }

        var settings = g.TryGetProperty("DefaultSettings", out var ds) && ds.TryGetUInt64(out var u) ? u : 0UL;
        return new LocalModGroup(index, Str(g, "Name"), Str(g, "Description"), string.IsNullOrEmpty(typeName) ? "Single" : typeName, type,
            Int(g, "Priority"), settings, options);
    }

    private static LocalModOption ReadOption(JsonElement o, int index, string modDirectory, Budget budget, bool isDefault)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o.TryGetProperty("Files", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String || !PathSafety.TryNormalizeGamePath(entry.Name, out var gamePath))
                    continue;
                var relative = entry.Value.GetString()!.Replace('\\', '/');
                // Mod files are always relative to the mod folder; drive letters and traversal are rejected on every platform.
                if (relative.Contains(':') || !PathSafety.TryCombineUnderRoot(modDirectory, relative, out var full))
                    continue;
                budget.Take();
                files[gamePath] = full;
            }

        return new LocalModOption(index, isDefault ? "Default" : Str(o, "Name"), Str(o, "Description"), Int(o, "Priority"), files);
    }

    private static JsonDocument Parse(string path, ModLibraryLimits limits)
    {
        var info = new FileInfo(path);
        if (info.Length > limits.MaxJsonBytes)
            throw new InvalidDataException($"{info.Name} is larger than {limits.MaxJsonBytes} bytes.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream, DocumentOptions);
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : 0;

    private sealed class Budget(int remaining)
    {
        public void Take()
        {
            if (--remaining < 0)
                throw new InvalidDataException("too many file redirections.");
        }
    }
}
