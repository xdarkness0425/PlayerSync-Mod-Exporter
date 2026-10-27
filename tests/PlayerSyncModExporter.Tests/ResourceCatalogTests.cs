using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Metadata;
using PlayerSyncModExporter.Core.Model;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

public sealed class ResourceCatalogTests : IDisposable
{
    private readonly TestFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AdditionalDirectory_FindsOnlyVerifiedSelectedPairFiles()
    {
        var folder = Path.Combine(_fx.Root, "ResourceCopy");
        Directory.CreateDirectory(folder);
        var bytes = TestFixture.RandomBytes(123, 801);
        var hash = TestFixture.Sha1(bytes);
        var file = Path.Combine(folder, hash.ToLowerInvariant() + ".mdl");
        File.WriteAllBytes(file, bytes);
        var other = Path.Combine(folder, TestFixture.Sha1([99]).ToLowerInvariant() + ".mdl");
        File.WriteAllBytes(other, [99]);
        var before = File.GetLastWriteTimeUtc(file);
        var analyzer = new AssetAnalyzer([new ReadOnlyResourceFolderLocator(folder)]);
        var result = await analyzer.AnalyzeAsync(TestFixture.Character([
            new(Player, hash, ["chara/human/test.mdl"])
        ]));
        var asset = Assert.Single(result.Assets);
        Assert.Equal(AssetStatus.Available, asset.Status);
        Assert.Equal(AssetProvenance.AdditionalResourceDirectory, asset.Provenance);
        Assert.True(asset.IsOriginalContent);
        Assert.Equal(file, asset.SourcePath);
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task AdditionalDirectory_RejectsWrongBytesAndForbiddenFiles()
    {
        var folder = Path.Combine(_fx.Root, "ResourceCopy");
        Directory.CreateDirectory(folder);
        var hash = TestFixture.Sha1(TestFixture.RandomBytes(18, 802));
        File.WriteAllBytes(Path.Combine(folder, hash.ToLowerInvariant() + ".mdl"), [1, 2, 3]);
        var analyzer = new AssetAnalyzer([new ReadOnlyResourceFolderLocator(folder)]);
        var mismatch = await analyzer.AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/human/test.mdl"])]));
        Assert.Equal(AssetStatus.HashMismatch, Assert.Single(mismatch.Assets).Status);
        var forbidden = await analyzer.AnalyzeAsync(TestFixture.Character(
            [new(Player, hash, ["chara/human/test.mdl"])], b => b.Forbidden.Add(hash)));
        Assert.Equal(AssetStatus.ForbiddenByServer, Assert.Single(forbidden.Assets).Status);
    }

    [Fact]
    public void Catalog_PreservesCandidateAmbiguityAndCountsDistinctPaths()
    {
        var mod = new LocalMod
        {
            Directory = "/local/one", FolderName = "one", Name = "Example", Default = new LocalModOption(
                -1, "Default", "", 0, new Dictionary<string, string>()), Groups = []
        };
        var shared = new PathAttribution
        {
            GamePath = "chara/a/x.mdl",
            Classification = MetadataClassification.Unavailable,
            CandidateMods = ["one", "two"],
        };
        var unique = new PathAttribution
        {
            GamePath = "chara/a/y.mdl",
            Classification = MetadataClassification.Recovered,
            Mod = mod, CandidateMods = ["one"],
        };
        var recovery = new MetadataRecoveryResult
        {
            LibraryAvailable = true,
            SourceMods = [new RecoveredSourceMod { Mod = mod, MatchedPaths = 1, Groups = [] }],
            Paths = new Dictionary<(string, string), PathAttribution>
            {
                [("HASH1", shared.GamePath)] = shared,
                [("HASH2", shared.GamePath)] = shared,
                [("HASH3", unique.GamePath)] = unique,
            }
        };
        var catalog = ModFingerprintCatalog.From(recovery);
        var one = Assert.Single(catalog, c => c.ModFolder == "one");
        Assert.Equal("Example", one.ModName);
        Assert.Equal(2, one.MatchedPaths);
        Assert.Equal(1, one.AmbiguousPaths);
        var two = Assert.Single(catalog, c => c.ModFolder == "two");
        Assert.Equal(1, two.MatchedPaths);
        Assert.Equal(1, two.AmbiguousPaths);
    }

    [Fact]
    public void Locator_InvalidHashOrExtensionNeverProbes()
    {
        var locator = new ReadOnlyResourceFolderLocator(_fx.CacheFolder);
        Assert.Empty(locator.Locate("../../etc", "mdl"));
        Assert.Empty(locator.Locate(new string('A', 40), "../mdl"));
        Assert.Empty(locator.Locate(new string('A', 40), ""));
    }
}
