using System.Collections;
using System.Reflection;
using System.Text.Json;
using PlayerSyncModExporter.Core.Hashing;

namespace PlayerSyncModExporter.Core.Model;

/// <summary>
/// Converts PlayerSync's <c>MareSynchronos.API.Data.CharacterData</c> (MareAPI/MareSynchronosAPI/Data/CharacterData.cs)
/// into a <see cref="CapturedCharacter"/> by reading public properties through reflection. PlayerSync does not
/// publish an API assembly for other plugins, so the shape is read by name and every missing member is reported
/// instead of guessed. The object is never modified.
/// </summary>
public static class PlayerSyncDataReader
{
    public sealed record ReadOptions
    {
        public required string Label { get; init; }
        public string? SourceVersion { get; init; }
        public IReadOnlyDictionary<string, string>? CompressionRedirects { get; init; }
        public IReadOnlySet<string>? ServerForbiddenHashes { get; init; }
        public DateTimeOffset? CapturedAtUtc { get; init; }
    }

    /// <param name="received">The data exactly as received (<c>Pair.LastReceivedCharacterData</c>).</param>
    /// <param name="applied">
    /// The same data after PlayerSync's own permission filter (<c>Pair.RemoveNotSyncedFiles</c>), or null if the
    /// filter could not be run. When null, every file is treated as synced and a warning is recorded.
    /// </param>
    public static CapturedCharacter Read(object received, object? applied, ReadOptions options)
    {
        var warnings = new List<string>();
        var receivedFiles = ReadReplacements(received, warnings, "received data");
        List<(CapturedFileReplacement? File, CapturedFileSwap? Swap)> appliedFiles;
        if (applied != null)
        {
            appliedFiles = ReadReplacements(applied, warnings, "filtered data");
        }
        else
        {
            appliedFiles = receivedFiles;
            warnings.Add("PlayerSync's permission filter could not be evaluated; sound/animation/VFX filters were not applied to this capture.");
        }

        var appliedKeys = appliedFiles.Where(x => x.File != null)
            .Select(x => Key(x.File!)).ToHashSet();
        var notSynced = receivedFiles.Where(x => x.File != null && !appliedKeys.Contains(Key(x.File!)))
            .Select(x => new NotSyncedFile(x.File!, NotSyncedReason.PlayerSyncFilter))
            .ToList();

        return new CapturedCharacter
        {
            Label = options.Label,
            CapturedAtUtc = options.CapturedAtUtc ?? DateTimeOffset.UtcNow,
            Source = "PlayerSync",
            SourceVersion = options.SourceVersion,
            Files = appliedFiles.Where(x => x.File != null).Select(x => x.File!).ToList(),
            FileSwaps = appliedFiles.Where(x => x.Swap != null).Select(x => x.Swap!).ToList(),
            NotSyncedFiles = notSynced,
            CompressionRedirects = options.CompressionRedirects ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ServerForbiddenHashes = options.ServerForbiddenHashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            GlamourerData = ReadPlayerString(received, "GlamourerData", warnings),
            CustomizePlusData = ReadPlayerString(received, "CustomizePlusData", warnings),
            ManipulationData = GetProperty(received, "ManipulationData") as string ?? string.Empty,
            CaptureWarnings = warnings,
        };
    }

    /// <summary>Deep-copies a PlayerSync data object via System.Text.Json so filters that mutate their input never touch PlayerSync's copy.</summary>
    public static object? JsonClone(object source)
    {
        var type = source.GetType();
        var json = JsonSerializer.Serialize(source, type);
        return JsonSerializer.Deserialize(json, type);
    }

    private static (SyncObjectKind, string, string) Key(CapturedFileReplacement f)
        => (f.Kind, f.Hash.ToUpperInvariant(), string.Join('\n', f.GamePaths.Select(p => p.ToLowerInvariant()).Order(StringComparer.Ordinal)));

    private static List<(CapturedFileReplacement? File, CapturedFileSwap? Swap)> ReadReplacements(object data, List<string> warnings, string what)
    {
        var result = new List<(CapturedFileReplacement?, CapturedFileSwap?)>();
        if (GetProperty(data, "FileReplacements") is not IDictionary dict)
        {
            warnings.Add($"The {what} has no FileReplacements dictionary (incompatible PlayerSync version?).");
            return result;
        }

        foreach (DictionaryEntry kv in dict)
        {
            if (!TryGetKind(kv.Key, out var kind))
            {
                warnings.Add($"Unknown object kind '{kv.Key}' in {what}; its files were ignored.");
                continue;
            }

            if (kv.Value is not IEnumerable list)
                continue;

            foreach (var item in list)
            {
                if (item == null)
                    continue;
                var hash = (GetProperty(item, "Hash") as string ?? string.Empty).Trim();
                var swap = (GetProperty(item, "FileSwapPath") as string ?? string.Empty).Trim();
                var paths = ReadStrings(GetProperty(item, "GamePaths"));
                if (paths.Count == 0)
                {
                    warnings.Add($"An entry in {what} ({kind}) has no game paths and was ignored.");
                    continue;
                }

                if (!string.IsNullOrEmpty(swap))
                    result.Add((null, new CapturedFileSwap(kind, swap, paths)));
                else
                    result.Add((new CapturedFileReplacement(kind, ContentHash.IsValid(hash) ? ContentHash.Normalize(hash) : hash, paths), null));
            }
        }

        return result;
    }

    private static string ReadPlayerString(object data, string property, List<string> warnings)
    {
        var value = GetProperty(data, property);
        if (value is null)
            return string.Empty;
        if (value is string s)
            return s;
        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry kv in dict)
                if (TryGetKind(kv.Key, out var kind) && kind == SyncObjectKind.Player)
                    return kv.Value as string ?? string.Empty;
            return string.Empty;
        }

        warnings.Add($"{property} has an unexpected type {value.GetType().Name}.");
        return string.Empty;
    }

    private static List<string> ReadStrings(object? value) => value switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => [s],
        IEnumerable e => e.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
        _ => [],
    };

    public static bool TryGetKind(object? key, out SyncObjectKind kind)
    {
        kind = SyncObjectKind.Player;
        if (key == null)
            return false;
        try
        {
            var value = key is Enum ? Convert.ToInt32(key, System.Globalization.CultureInfo.InvariantCulture)
                : key is string str && Enum.TryParse<SyncObjectKind>(str, out var parsed) ? (int)parsed
                : Convert.ToInt32(key, System.Globalization.CultureInfo.InvariantCulture);
            if (!Enum.IsDefined(typeof(SyncObjectKind), value))
                return false;
            kind = (SyncObjectKind)value;
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    public static object? GetProperty(object target, string name)
    {
        var type = target.GetType();
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.GetIndexParameters().Length == 0)
            return prop.GetValue(target);
        return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
    }
}
