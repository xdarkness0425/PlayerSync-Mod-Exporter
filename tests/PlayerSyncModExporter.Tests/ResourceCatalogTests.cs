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
    public void Catalog_PreservesEveryCandidateAndPerOptionEvidence()
    {
        var evidence = new[]
        {
            new ModFingerprintEvidence("H1", "chara/a/x.mdl", "one", "Example", 0, "Color", 0, "Red"),
            new ModFingerprintEvidence("H1", "chara/a/x.mdl", "one", "Example", 0, "Color", 1, "Blue"),
            new ModFingerprintEvidence("H1", "chara/a/x.mdl", "two", "Other", -1, null, -1, null),
            new ModFingerprintEvidence("H2", "chara/a/y.mtrl", "one", "Example", 0, "Color", 0, "Red"),
            new ModFingerprintEvidence("H3", "chara/a/z.tex", "one", "Example", -1, null, -1, null),
        };
        var recovery = new MetadataRecoveryResult { LibraryAvailable = true, FingerprintEvidence = evidence };
        var catalog = ModFingerprintCatalog.From(recovery);
        var one = Assert.Single(catalog, c => c.ModFolder == "one");
        Assert.Equal("Example", one.ModName);
        Assert.Equal(3, one.MatchedPaths);
        Assert.Equal(2, one.DistinctivePaths);
        Assert.Equal(1, one.AmbiguousPaths);
        Assert.Equal(3, one.OptionMatches.Count);
        var red = Assert.Single(one.OptionMatches, o => o.OptionName == "Red");
        Assert.Equal(2, red.MatchedPaths);
        Assert.Equal(1, red.SharedPaths);
        var blue = Assert.Single(one.OptionMatches, o => o.OptionName == "Blue");
        Assert.Equal(1, blue.MatchedPaths);
        Assert.Equal(1, blue.SharedPaths);
        var two = Assert.Single(catalog, c => c.ModFolder == "two");
        Assert.Equal(1, two.MatchedPaths);
        Assert.Equal(0, two.DistinctivePaths);
        Assert.Equal(1, two.AmbiguousPaths);
        Assert.Equal(2, one.Files.Single(f => f.GamePath == "chara/a/x.mdl").CatalogModCount);
    }

    [Theory]
    [InlineData("chara/a/model.mdl", FingerprintResourceKind.Model)]
    [InlineData("chara/a/mat.mtrl", FingerprintResourceKind.Material)]
    [InlineData("chara/a/diffuse.tex", FingerprintResourceKind.Texture)]
    [InlineData("chara/a/emote.pap", FingerprintResourceKind.Animation)]
    [InlineData("chara/a/cast.avfx", FingerprintResourceKind.Effect)]
    [InlineData("sound/a/test.scd", FingerprintResourceKind.Audio)]
    [InlineData("chara/a/other.bin", FingerprintResourceKind.Other)]
    public void Catalog_ClassifiesResourcesByFileType(string path, FingerprintResourceKind expected)
        => Assert.Equal(expected, ModFingerprintCatalog.Classify(path));

    [Fact]
    public void Locator_InvalidHashOrExtensionNeverProbes()
    {
        var locator = new ReadOnlyResourceFolderLocator(_fx.CacheFolder);
        Assert.Empty(locator.Locate("../../etc", "mdl"));
        Assert.Empty(locator.Locate(new string('A', 40), "../mdl"));
        Assert.Empty(locator.Locate(new string('A', 40), ""));
    }
}
