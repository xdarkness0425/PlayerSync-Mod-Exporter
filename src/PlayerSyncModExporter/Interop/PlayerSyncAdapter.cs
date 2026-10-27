using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Model;

namespace PlayerSyncModExporter.Interop;

public enum PlayerSyncState
{
    NotInstalled,
    NotLoaded,
    Incompatible,
    Ready,
}

public sealed record PlayerSyncStatus(PlayerSyncState State, string? Version, IReadOnlyList<string> Messages)
{
    public bool IsReady => State == PlayerSyncState.Ready;
}

/// <summary>A visible, paired PlayerSync user as shown in the selection list. Holds no account identifiers.</summary>
public sealed record SyncedCharacter(string PlayerName, nint Address, bool IsPaused, bool HasData, object PairHandle);

/// <summary>
/// The only place that knows about PlayerSync internals. PlayerSync (InternalName "MareSempiterne",
/// namespace MareSynchronos) exposes just two IPC functions to other plugins: PlayerSync.GetHandledAddresses and
/// PlayerSync.LoadMcdf(Async). Character data and the file cache index are not exposed, so this adapter reads them
/// through reflection, the same approach Snappy uses. Everything here is read-only: no PlayerSync method that writes
/// state is ever called, and the one method invoked (Pair.RemoveNotSyncedFiles) only receives a private copy.
/// All members are verified against PlayerSync 1.15.5.9 (MareSynchronosClient @ b2f8ce8); see docs/PLAYERSYNC_INTERNALS.md.
/// </summary>
public sealed class PlayerSyncAdapter : IDisposable
{
    public const string InternalName = "MareSempiterne";
    public const string TestedVersion = "1.15.5.9";
    private const string HandledAddressesIpc = "PlayerSync.GetHandledAddresses";
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<List<nint>> _handledAddresses;

    private object? _pluginInstance;
    private object? _pairManager;
    private object? _fileCacheManager;
    private object? _transferOrchestrator;
    private MethodInfo? _getVisiblePairs;
    private MethodInfo? _getAllFileCachesByHash;
    private MethodInfo? _removeNotSyncedFiles;
    private PlayerSyncStatus _status = new(PlayerSyncState.NotInstalled, null, []);

    public PlayerSyncAdapter(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi = pi;
        _log = log;
        _handledAddresses = pi.GetIpcSubscriber<List<nint>>(HandledAddressesIpc);
        _pi.ActivePluginsChanged += OnActivePluginsChanged;
    }

    public PlayerSyncStatus Status => _status;

    public void Dispose() => _pi.ActivePluginsChanged -= OnActivePluginsChanged;

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (args.AffectedInternalNames.Contains(InternalName))
            Reset();
    }

    public void Reset()
    {
        _pluginInstance = _pairManager = _fileCacheManager = _transferOrchestrator = null;
        _getVisiblePairs = _getAllFileCachesByHash = _removeNotSyncedFiles = null;
    }

    /// <summary>Detects PlayerSync and binds to its services. Cheap when already bound; call before any other member.</summary>
    public PlayerSyncStatus Refresh()
    {
        var messages = new List<string>();
        var exposed = _pi.InstalledPlugins.FirstOrDefault(p => p.InternalName == InternalName);
        if (exposed == null)
            return _status = new PlayerSyncStatus(PlayerSyncState.NotInstalled, null, ["PlayerSync is not installed."]);

        var version = exposed.Version.ToString();
        if (!exposed.IsLoaded)
        {
            Reset();
            return _status = new PlayerSyncStatus(PlayerSyncState.NotLoaded, version, ["PlayerSync is installed but not enabled."]);
        }

        if (_pairManager != null && _getVisiblePairs != null)
            return _status;

        try
        {
            Bind(messages);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Binding to PlayerSync failed");
            messages.Add("Could not read PlayerSync's internals: " + e.Message);
            Reset();
        }

        if (version != TestedVersion)
            messages.Add($"PlayerSync {version} has not been tested with this exporter (tested: {TestedVersion}).");

        var state = _pairManager != null && _getVisiblePairs != null ? PlayerSyncState.Ready : PlayerSyncState.Incompatible;
        if (state == PlayerSyncState.Incompatible)
            messages.Add("This PlayerSync version is not compatible: the pair list could not be found.");
        return _status = new PlayerSyncStatus(state, version, messages);
    }

    private void Bind(List<string> messages)
    {
        _pluginInstance = GetPluginInstance() ?? throw new InvalidOperationException("PlayerSync's plugin instance is not available yet.");
        var host = GetMember(_pluginInstance, "_host") ?? throw new InvalidOperationException("PlayerSync's service host was not found.");
        if (GetMember(host, "Services") is not IServiceProvider services)
            throw new InvalidOperationException("PlayerSync's service provider was not found.");

        var asm = _pluginInstance.GetType().Assembly;
        object? Service(string typeName)
        {
            var type = asm.GetType(typeName);
            if (type == null)
            {
                messages.Add($"Type {typeName} was not found.");
                return null;
            }

            return services.GetService(type);
        }

        _pairManager = Service("MareSynchronos.PlayerData.Pairs.PairManager");
        _fileCacheManager = Service("MareSynchronos.FileCache.FileCacheManager");
        _transferOrchestrator = Service("MareSynchronos.WebAPI.Files.FileTransferOrchestrator");

        _getVisiblePairs = _pairManager?.GetType().GetMethod("GetVisiblePairs", AnyInstance, Type.EmptyTypes);
        _getAllFileCachesByHash = _fileCacheManager?.GetType().GetMethod("GetAllFileCachesByHash", AnyInstance,
            [typeof(string), typeof(bool), typeof(bool)]);
        var pairType = asm.GetType("MareSynchronos.PlayerData.Pairs.Pair");
        _removeNotSyncedFiles = pairType?.GetMethod("RemoveNotSyncedFiles", AnyInstance);

        if (_fileCacheManager == null || _getAllFileCachesByHash == null)
            messages.Add("PlayerSync's file cache index is unavailable; falling back to scanning the cache folder and FileCache.csv.");
        if (_removeNotSyncedFiles == null)
            messages.Add("PlayerSync's permission filter was not found; files PlayerSync filters out cannot be identified.");
        if (_transferOrchestrator == null)
            messages.Add("PlayerSync's forbidden-file list is unavailable.");
    }

    /// <summary>
    /// Dalamud only hands other plugins an IExposedPlugin (no instance). The instance is read from Dalamud's
    /// PluginManager (Dalamud.Service&lt;PluginManager&gt;.Get().InstalledPlugins -> LocalPlugin.instance).
    /// </summary>
    private object? GetPluginInstance()
    {
        var dalamud = typeof(IDalamudPluginInterface).Assembly;
        var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
        var serviceType = dalamud.GetType("Dalamud.Service`1")?.MakeGenericType(managerType!);
        var manager = serviceType?.GetMethod("Get", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        if (manager == null || GetMember(manager, "InstalledPlugins") is not IEnumerable plugins)
            return null;

        foreach (var plugin in plugins)
            if (plugin != null && GetMember(plugin, "InternalName") as string == InternalName)
                return GetMember(plugin, "instance");
        return null;
    }

    /// <summary>
    /// Visible pairs that PlayerSync actively handles. Must be called on the framework thread.
    /// Intersected with the public PlayerSync.GetHandledAddresses IPC when available.
    /// </summary>
    public List<SyncedCharacter> GetVisiblePairs()
    {
        var result = new List<SyncedCharacter>();
        if (Refresh().State != PlayerSyncState.Ready)
            return result;

        HashSet<nint>? handled = null;
        try
        {
            if (_handledAddresses.HasFunction)
                handled = _handledAddresses.InvokeFunc().Where(a => a != nint.Zero).ToHashSet();
        }
        catch (Exception e)
        {
            _log.Debug($"{HandledAddressesIpc} failed: {e.Message}");
        }

        if (_getVisiblePairs!.Invoke(_pairManager, null) is not IEnumerable pairs)
            return result;

        foreach (var pair in pairs)
        {
            if (pair == null)
                continue;
            var address = GetMember(pair, "Address") is nint a ? a : nint.Zero;
            if (address == nint.Zero || (handled != null && !handled.Contains(address)))
                continue;
            var name = GetMember(pair, "PlayerName") as string;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result.Add(new SyncedCharacter(name, address, GetMember(pair, "IsPaused") is true,
                GetMember(pair, "LastReceivedCharacterData") != null, pair));
        }

        return result.OrderBy(p => p.PlayerName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Takes an immutable snapshot of what PlayerSync last received for this pair and what it applies after
    /// its permission filters. Must be called on the framework thread. Never modifies PlayerSync's objects.
    /// </summary>
    public CapturedCharacter? Capture(SyncedCharacter character, string label, out string? error)
    {
        error = null;
        var pair = character.PairHandle;
        if (GetMember(pair, "IsPaused") is true)
        {
            error = "This pair is paused in PlayerSync. Unpause them in PlayerSync first; the exporter does not bypass pauses.";
            return null;
        }

        if (GetMember(pair, "IsVisible") is not true || (GetMember(pair, "Address") is nint a ? a : 0) != character.Address)
        {
            error = "The character is no longer visible or has changed. Refresh the list and select them again.";
            return null;
        }

        var received = GetMember(pair, "LastReceivedCharacterData");
        if (received == null)
        {
            error = "PlayerSync has not received any appearance data for this character yet.";
            return null;
        }

        object? applied = null;
        if (_removeNotSyncedFiles != null)
        {
            try
            {
                // RemoveNotSyncedFiles mutates its argument, so it only ever gets a deep copy.
                var copy = Core.Model.PlayerSyncDataReader.JsonClone(received);
                if (copy != null)
                    applied = _removeNotSyncedFiles.Invoke(pair, [copy]);
            }
            catch (Exception e) when (e is TargetInvocationException or JsonException or NotSupportedException or ArgumentException)
            {
                _log.Warning(e, "Evaluating PlayerSync's permission filter failed");
            }
        }

        return Core.Model.PlayerSyncDataReader.Read(received, applied, new Core.Model.PlayerSyncDataReader.ReadOptions
        {
            Label = label,
            SourceVersion = _status.Version,
            CompressionRedirects = ReadCompressionRedirects(pair),
            ServerForbiddenHashes = ReadForbiddenHashes(),
        });
    }

    private Dictionary<string, string> ReadCompressionRedirects(object pair)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var handler = GetMember(pair, "CachedPlayer");
            if (handler != null && GetMember(handler, "ActiveCompressionRedirects") is IEnumerable redirects)
                foreach (var item in redirects)
                    if (item is KeyValuePair<string, string> kv)
                        result[kv.Key] = kv.Value;
        }
        catch (Exception e) when (e is TargetInvocationException or InvalidOperationException)
        {
            _log.Debug($"Reading compression redirects failed: {e.Message}");
        }

        return result;
    }

    private HashSet<string> ReadForbiddenHashes()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_transferOrchestrator == null || GetMember(_transferOrchestrator, "ForbiddenTransfers") is not IList list)
            return result;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var copy = new object?[list.Count];
                list.CopyTo(copy, 0);
                foreach (var transfer in copy)
                    if (transfer != null && GetMember(transfer, "Hash") is string h)
                        result.Add(h);
                return result;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                result.Clear(); // the list changed while copying; retry
            }
        }

        return result;
    }

    /// <summary>PlayerSync's configured cache folder (FileCacheManager.CacheFolder), if known.</summary>
    public string? GetCacheFolder()
        => _fileCacheManager != null ? GetMember(_fileCacheManager, "CacheFolder") as string : ReadCacheFolderFromConfig();

    /// <summary>Read-only fallback: the CacheFolder setting in pluginConfigs/MareSempiterne/config.json (no secrets live there).</summary>
    private string? ReadCacheFolderFromConfig()
    {
        try
        {
            var path = Path.Combine(GetPlayerSyncConfigDirectory(), "config.json");
            if (!File.Exists(path))
                return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.TryGetProperty("CacheFolder", out var v) ? v.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string GetPlayerSyncConfigDirectory()
        => Path.Combine(_pi.ConfigDirectory.Parent?.FullName ?? string.Empty, InternalName);

    /// <summary>Locators in preference order: PlayerSync's live index, the cache folder itself, then a read-only FileCache.csv snapshot.</summary>
    public List<IAssetLocator> CreateLocators(string? penumbraModDirectory)
    {
        var locators = new List<IAssetLocator>();
        var cacheFolder = GetCacheFolder();
        var roots = new[] { cacheFolder, penumbraModDirectory }.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).ToArray();

        if (_fileCacheManager != null && _getAllFileCachesByHash != null)
            locators.Add(new DelegateLocator("PlayerSync live index", (hash, _) => LocateLive(hash, roots)));
        if (!string.IsNullOrWhiteSpace(cacheFolder))
            locators.Add(new CacheFolderLocator(cacheFolder));

        var csv = Path.Combine(GetPlayerSyncConfigDirectory(), "FileCache.csv");
        if (File.Exists(csv))
            locators.Add(new FileCacheCsvLocator(FileCacheCsv.Read(csv), cacheFolder, penumbraModDirectory));
        return locators;
    }

    private IEnumerable<LocatedCandidate> LocateLive(string hash, string[] roots)
    {
        // validate: false keeps the call read-only. With validate: true PlayerSync would rehash and rewrite its index.
        if (_getAllFileCachesByHash!.Invoke(_fileCacheManager, [hash, false, false]) is not IEnumerable entries)
            yield break;

        foreach (var entry in entries.Cast<object?>().ToList())
        {
            if (entry == null || GetMember(entry, "ResolvedFilepath") is not string path || string.IsNullOrEmpty(path))
                continue;
            if (!roots.Any(r => Core.IO.PathSafety.IsUnderRoot(r, path)))
                continue;
            var isCache = GetMember(entry, "IsCacheEntry") is true;
            yield return new LocatedCandidate(path, isCache ? AssetProvenance.PlayerSyncCache : AssetProvenance.PenumbraModDirectory);
        }
    }

    private static object? GetMember(object target, string name)
    {
        try
        {
            return Core.Model.PlayerSyncDataReader.GetProperty(target, name);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}
