using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace PlayerSyncModExporter.Core.Meta;

/// <summary>
/// Penumbra meta manipulations (EQP, EQDP, EST, IMC, RSP, GMP, ATCH, SHP, ATR, global EQP) are sent by
/// PlayerSync as Penumbra's compressed string (Penumbra MetaApi.CompressMetaManipulations): base64 of a
/// gzip stream whose first byte is a format version. Version 0 is a JSON array in the same shape as a
/// mod's "Manipulations" field; versions 1 and 2 are binary struct dumps that only Penumbra can decode reliably.
/// </summary>
public sealed record MetaManipulationInfo(bool IsEmpty, bool IsDecodable, byte? Version, string? Error);

public interface IMetaManipulationConverter
{
    string Name { get; }

    /// <summary>Converts to a JSON array suitable for a Penumbra mod's "Manipulations" field.</summary>
    bool TryConvert(string compressedBase64, out string manipulationsJson, out string? error);
}

public static class MetaManipulationCodec
{
    public static MetaManipulationInfo Inspect(string? compressedBase64)
    {
        if (string.IsNullOrEmpty(compressedBase64))
            return new MetaManipulationInfo(true, true, null, null);

        if (!TryDecompress(compressedBase64, out var data, out var error))
            return new MetaManipulationInfo(false, false, null, error);

        if (data.Length == 0)
            return new MetaManipulationInfo(false, false, null, "Decompressed meta data is empty.");

        return new MetaManipulationInfo(false, true, data[0], null);
    }

    /// <summary>Independent decoder for the version-0 (JSON) format.</summary>
    public static bool TryDecodeVersion0(string compressedBase64, out string json, out string? error)
    {
        json = "[]";
        if (!TryDecompress(compressedBase64, out var data, out error))
            return false;
        if (data.Length == 0 || data[0] != 0)
        {
            error = data.Length == 0 ? "Empty meta data." : $"Meta data uses binary format version {data[0]}, which needs Penumbra to decode.";
            return false;
        }

        try
        {
            var node = JsonNode.Parse(Encoding.UTF8.GetString(data, 1, data.Length - 1));
            if (node is not JsonArray array || !IsManipulationArray(array))
            {
                error = "Version-0 meta data is not a manipulation array.";
                return false;
            }

            json = array.ToJsonString();
            return true;
        }
        catch (System.Text.Json.JsonException e)
        {
            error = "Version-0 meta data is not valid JSON: " + e.Message;
            return false;
        }
    }

    /// <summary>Checks the shape Penumbra expects: an array of objects each carrying "Type" and "Manipulation".</summary>
    public static bool IsManipulationArray(JsonArray array)
        => array.All(item => item is JsonObject o && o["Type"] is JsonValue && o["Manipulation"] is JsonObject);

    public static int CountEntries(string manipulationsJson)
        => JsonNode.Parse(manipulationsJson) is JsonArray a ? a.Count : 0;

    /// <summary>Builds a compressed base64 string in format version 0 (used by tests and fixtures).</summary>
    public static string EncodeVersion0(string manipulationsJson)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.WriteByte(0);
            var bytes = Encoding.UTF8.GetBytes(manipulationsJson);
            gz.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    private static bool TryDecompress(string compressedBase64, out byte[] data, out string? error)
    {
        data = [];
        error = null;
        try
        {
            var bytes = Convert.FromBase64String(compressedBase64);
            using var input = new MemoryStream(bytes);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            // Meta data is small; cap it so malformed input cannot exhaust memory.
            var buffer = new byte[81920];
            int read;
            while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > 64L * 1024 * 1024)
                {
                    error = "Meta data is unreasonably large.";
                    return false;
                }
            }

            data = output.ToArray();
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException)
        {
            error = "Meta data is not valid compressed base64: " + e.Message;
            return false;
        }
    }
}

/// <summary>Converter that only handles the JSON (version 0) format, without any Penumbra dependency.</summary>
public sealed class Version0MetaConverter : IMetaManipulationConverter
{
    public string Name => "Built-in (format version 0 only)";

    public bool TryConvert(string compressedBase64, out string manipulationsJson, out string? error)
        => MetaManipulationCodec.TryDecodeVersion0(compressedBase64, out manipulationsJson, out error);
}

/// <summary>Tries each converter in order and returns the first success.</summary>
public sealed class ChainedMetaConverter : IMetaManipulationConverter
{
    private readonly IReadOnlyList<IMetaManipulationConverter> _converters;

    public ChainedMetaConverter(params IMetaManipulationConverter[] converters) => _converters = converters;

    public string Name => string.Join(" -> ", _converters.Select(c => c.Name));

    public bool TryConvert(string compressedBase64, out string manipulationsJson, out string? error)
    {
        var errors = new List<string>();
        foreach (var converter in _converters)
        {
            if (converter.TryConvert(compressedBase64, out manipulationsJson, out error))
                return true;
            if (error != null)
                errors.Add($"{converter.Name}: {error}");
        }

        manipulationsJson = "[]";
        error = string.Join(" | ", errors);
        return false;
    }
}
