using System.Security.Cryptography;
using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using PlayerSyncModExporter.Core.Safety;

namespace PlayerSyncModExporter.Tests;

/// <summary>
/// A throwaway directory tree that imitates PlayerSync's storage: a cache folder with content-addressed
/// files named "&lt;sha1 lower&gt;.&lt;ext&gt;", a Penumbra mod directory and an export destination.
/// </summary>
public sealed class TestFixture : IDisposable
{
    public TestFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "psme-tests-" + Guid.NewGuid().ToString("N"));
        CacheFolder = Path.Combine(Root, "PlayerSyncCache");
        PenumbraFolder = Path.Combine(Root, "Penumbra");
        ExportRoot = Path.Combine(Root, "Exports");
        Directory.CreateDirectory(CacheFolder);
        Directory.CreateDirectory(PenumbraFolder);
        Directory.CreateDirectory(ExportRoot);
    }

    public string Root { get; }
    public string CacheFolder { get; }
    public string PenumbraFolder { get; }
    public string ExportRoot { get; }

    public static ExportConsent FullConsent => new()
    {
        PermissionConfirmed = true, RedistributionAcknowledged = true, UnknownSharingStatusAcknowledged = true,
    };

    public static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    /// <summary>Writes content into the cache the way PlayerSync's FileDownloadManager does and returns its hash.</summary>
    public string AddCacheFile(byte[] content, string extension)
    {
        var hash = Sha1(content);
        File.WriteAllBytes(Path.Combine(CacheFolder, hash.ToLowerInvariant() + "." + extension), content);
        return hash;
    }

    public CacheFolderLocator Locator => new(CacheFolder);

    public AssetAnalyzer Analyzer(params IAssetLocator[] extra) => new([Locator, .. extra]);

    public static CapturedCharacter Character(IEnumerable<CapturedFileReplacement> files, Action<CapturedCharacterBuilder>? configure = null)
    {
        var b = new CapturedCharacterBuilder { Files = files.ToList() };
        configure?.Invoke(b);
        return b.Build();
    }

    public ExportRequest Request(AnalysisResult analysis, string name = "Test Character", Action<ExportRequestBuilder>? configure = null)
    {
        var b = new ExportRequestBuilder { Analysis = analysis, Destination = ExportRoot, Name = name };
        configure?.Invoke(b);
        return b.Build();
    }

    /// <summary>Snapshot of every cache file's bytes hash and timestamp, to prove exports never modify the cache.</summary>
    public Dictionary<string, (string Hash, DateTime Mtime)> SnapshotCache()
        => Directory.GetFiles(CacheFolder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Sha1(File.ReadAllBytes(f)), File.GetLastWriteTimeUtc(f)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class CapturedCharacterBuilder
{
    public List<CapturedFileReplacement> Files { get; set; } = [];
    public List<CapturedFileSwap> Swaps { get; set; } = [];
    public List<NotSyncedFile> NotSynced { get; set; } = [];
    public Dictionary<string, string> Redirects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Forbidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Manipulations { get; set; } = string.Empty;
    public string Glamourer { get; set; } = "Bh+LCAAAAAAAAAqrVkrNS0ktUrJSUkosTgYAHwKv7xIAAAA=";

    public CapturedCharacter Build() => new()
    {
        Label = "Test Character",
        CapturedAtUtc = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
        SourceVersion = "1.15.5.9",
        Files = Files,
        FileSwaps = Swaps,
        NotSyncedFiles = NotSynced,
        CompressionRedirects = Redirects,
        ServerForbiddenHashes = Forbidden,
        ManipulationData = Manipulations,
        GlamourerData = Glamourer,
        CustomizePlusData = string.Empty,
    };
}

public sealed class ExportRequestBuilder
{
    public required AnalysisResult Analysis { get; set; }
    public required string Destination { get; set; }
    public required string Name { get; set; }
    public ExportConsent Consent { get; set; } = TestFixture.FullConsent;
    public bool Pmp { get; set; } = true;
    public bool Mcdf { get; set; } = true;
    public bool NonPlayer { get; set; }
    public ExistingPackagePolicy Policy { get; set; } = ExistingPackagePolicy.Fail;

    public ExportRequest Build() => new()
    {
        Analysis = Analysis, DestinationRoot = Destination, PackageName = Name, Consent = Consent,
        WritePmp = Pmp, WriteMcdf = Mcdf, IncludeNonPlayerObjects = NonPlayer, ExistingPolicy = Policy,
        GeneratorVersion = "test",
    };
}
