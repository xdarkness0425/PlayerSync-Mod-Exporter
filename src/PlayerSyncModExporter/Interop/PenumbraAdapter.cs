using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PlayerSyncModExporter.Core.Meta;

namespace PlayerSyncModExporter.Interop;

/// <summary>
/// Penumbra access. Uses the supported Penumbra.Api IPC for everything Penumbra exposes, and reflection only
/// for converting meta manipulations, which Penumbra has no IPC for. Nothing here changes Penumbra state:
/// no collections are created, assigned or modified.
/// </summary>
public sealed class PenumbraAdapter
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;

    public PenumbraAdapter(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi = pi;
        _log = log;
        MetaConverter = new PenumbraReflectionMetaConverter(log);
    }

    public IMetaManipulationConverter MetaConverter { get; }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return new Penumbra.Api.IpcSubscribers.ApiVersion(_pi).Invoke().Item1 >= 5;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string? GetModDirectory()
    {
        try
        {
            var dir = new Penumbra.Api.IpcSubscribers.GetModDirectory(_pi).Invoke();
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception e)
        {
            _log.Debug($"Penumbra GetModDirectory failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The on-disk files Penumbra currently resolves for an actor (diagnostic cross-check only; animations and
    /// sounds only appear once they have been loaded). Must be called on the framework thread.
    /// </summary>
    public HashSet<string>? GetResolvedFilesFor(ushort objectIndex)
    {
        try
        {
            var result = new Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths(_pi).Invoke(objectIndex);
            if (result.Length == 0 || result[0] == null)
                return null;
            return result[0]!.Keys.Where(Path.IsPathRooted).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            _log.Debug($"Penumbra GetGameObjectResourcePaths failed: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// Converts Penumbra's compressed meta string into the JSON "Manipulations" array of a mod by calling
/// Penumbra's own decoder (Penumbra.Api.Api.MetaApi.ConvertManips) and serializer (MetaDictionary's
/// Newtonsoft converter) inside the loaded Penumbra assembly. This is the only way to decode the binary
/// formats (versions 1 and 2) exactly. It is internal Penumbra API and may break; failures are reported,
/// never guessed around.
/// </summary>
public sealed class PenumbraReflectionMetaConverter : IMetaManipulationConverter
{
    private readonly IPluginLog _log;

    public PenumbraReflectionMetaConverter(IPluginLog log) => _log = log;

    public string Name => "Penumbra MetaApi.ConvertManips (reflection)";

    public bool TryConvert(string compressedBase64, out string manipulationsJson, out string? error)
    {
        manipulationsJson = "[]";
        error = null;
        try
        {
            var penumbra = AppDomain.CurrentDomain.GetAssemblies()
                .LastOrDefault(a => a.GetName().Name == "Penumbra" && AssemblyLoadContext.GetLoadContext(a) is { } alc && !IsUnloading(alc));
            if (penumbra == null)
            {
                error = "Penumbra is not loaded.";
                return false;
            }

            var convert = penumbra.GetType("Penumbra.Api.Api.MetaApi")
                ?.GetMethod("ConvertManips", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (convert == null || convert.ReturnType != typeof(bool) || convert.GetParameters().Length != 3)
            {
                error = "This Penumbra version has no compatible MetaApi.ConvertManips.";
                return false;
            }

            object?[] args = [compressedBase64, null, null];
            if (convert.Invoke(null, args) is not true || args[1] is not { } dictionary)
            {
                error = "Penumbra could not decode the meta data.";
                return false;
            }

            // Serialize with the exact Newtonsoft.Json instance Penumbra's MetaDictionary converter is bound to.
            var converterAttribute = dictionary.GetType().GetCustomAttributes(false)
                .FirstOrDefault(a => a.GetType().FullName == "Newtonsoft.Json.JsonConverterAttribute");
            var serialize = converterAttribute?.GetType().Assembly.GetType("Newtonsoft.Json.JsonConvert")
                ?.GetMethod("SerializeObject", BindingFlags.Static | BindingFlags.Public, [typeof(object)]);
            if (serialize?.Invoke(null, [dictionary]) is not string json)
            {
                error = "Penumbra's meta serializer was not found.";
                return false;
            }

            if (JsonNode.Parse(json) is not JsonArray array || !MetaManipulationCodec.IsManipulationArray(array))
            {
                error = "Penumbra returned meta data in an unexpected shape.";
                return false;
            }

            manipulationsJson = array.ToJsonString();
            return true;
        }
        catch (Exception e)
        {
            _log.Warning(e, "Meta conversion through Penumbra failed");
            error = e.InnerException?.Message ?? e.Message;
            return false;
        }
    }

    private static bool IsUnloading(AssemblyLoadContext alc)
        => alc.IsCollectible && !alc.Assemblies.Any();
}
