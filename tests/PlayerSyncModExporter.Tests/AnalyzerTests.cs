using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Model;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

public class AnalyzerTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AllAssetsAvailable_AreVerifiedAndPreselected()
    {
        var tex = _fx.AddCacheFile(TestFixture.RandomBytes(4096, 1), "tex");
        var mdl = _fx.AddCacheFile(TestFixture.RandomBytes(2048, 2), "mdl");
        var c = TestFixture.Character([
            new(Player, tex, ["chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex"]),
            new(Player, mdl, ["chara/equipment/e0001/model/c0101e0001_top.mdl"]),
        ]);

        var result = await _fx.Analyzer().AnalyzeAsync(c);

        Assert.Equal(2, result.Assets.Count);
        Assert.All(result.Assets, a =>
        {
            Assert.Equal(AssetStatus.Available, a.Status);
            Assert.True(a.Include);
            Assert.True(a.IsOriginalContent);
            Assert.Equal(AssetProvenance.PlayerSyncCache, a.Provenance);
            Assert.Equal(a.Hash, a.ContentHash);
        });
        Assert.True(result.AllRequiredAvailable);
    }

    [Fact]
    public async Task MissingAsset_IsReportedNotSkipped()
    {
        var present = _fx.AddCacheFile(TestFixture.RandomBytes(100, 3), "tex");
        var missing = TestFixture.Sha1(TestFixture.RandomBytes(100, 4));
        var c = TestFixture.Character([
            new(Player, present, ["chara/a/b.tex"]),
            new(Player, missing, ["chara/a/c.tex"]),
        ]);

        var result = await _fx.Analyzer().AnalyzeAsync(c);

        var m = Assert.Single(result.Assets, a => a.Hash == missing);
        Assert.Equal(AssetStatus.Missing, m.Status);
        Assert.False(m.Include);
        Assert.False(m.IsExportable);
        Assert.False(result.AllRequiredAvailable);
    }

    [Fact]
    public async Task DuplicateHashes_AreMergedIntoOneAsset()
    {
        var hash = _fx.AddCacheFile(TestFixture.RandomBytes(500, 5), "tex");
        var c = TestFixture.Character([
            new(Player, hash, ["chara/a/one.tex"]),
            new(Player, hash.ToLowerInvariant(), ["chara/a/two.tex", "chara/a/one.tex"]),
            new(MinionOrMount, hash, ["chara/monster/m0001/x.tex"]),
        ]);

        var result = await _fx.Analyzer().AnalyzeAsync(c);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(3, asset.GamePaths.Count);
        Assert.Contains(asset.GamePaths, p => p.Kind == MinionOrMount);
    }

    [Fact]
    public async Task CorruptFile_IsHashMismatch()
    {
        var content = TestFixture.RandomBytes(1000, 6);
        var hash = _fx.AddCacheFile(content, "tex");
        var path = Path.Combine(_fx.CacheFolder, hash.ToLowerInvariant() + ".tex");
        content[10] ^= 0xFF;
        File.WriteAllBytes(path, content);

        var result = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/a/b.tex"])]));

        var a = Assert.Single(result.Assets);
        Assert.Equal(AssetStatus.HashMismatch, a.Status);
        Assert.False(a.IsExportable);
    }

    [Fact]
    public async Task InvalidMetadata_IsReported()
    {
        var good = _fx.AddCacheFile(TestFixture.RandomBytes(10, 7), "tex");
        var c = TestFixture.Character([
            new(Player, "not-a-hash", ["chara/a/b.tex"]),
            new(Player, "", ["chara/a/c.tex"]),
            new(Player, good, ["../../escape.tex", "C:/windows/x.tex", "/abs/x.tex", "chara/ok/fine.tex", "noextension/file"]),
            new(Player, TestFixture.Sha1([1, 2, 3]), ["..\\..\\x.tex"]),
        ]);

        var result = await _fx.Analyzer().AnalyzeAsync(c);

        Assert.Equal(3, result.Count(AssetStatus.InvalidMetadata));
        var ok = Assert.Single(result.Assets, a => a.Hash == good);
        Assert.Equal(AssetStatus.Available, ok.Status);
        Assert.Equal("chara/ok/fine.tex", Assert.Single(ok.GamePaths).GamePath);
        Assert.Contains(result.Warnings, w => w.Contains("escape.tex"));
    }

    [Fact]
    public async Task EmptyCharacter_ProducesNoAssets()
    {
        var result = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([]));
        Assert.Empty(result.Assets);
    }

    [Fact]
    public async Task CompressedAlternate_IsUsedButMarkedNotOriginal()
    {
        var original = TestFixture.Sha1(TestFixture.RandomBytes(4000, 8));
        var alt = _fx.AddCacheFile(TestFixture.RandomBytes(1000, 9), "tex");
        var c = TestFixture.Character([new(Player, original, ["chara/a/b.tex"])], b => b.Redirects[original] = alt);

        var result = await _fx.Analyzer().AnalyzeAsync(c);

        var a = Assert.Single(result.Assets);
        Assert.Equal(AssetStatus.Available, a.Status);
        Assert.False(a.IsOriginalContent);
        Assert.Equal(alt, a.ContentHash);
        Assert.Equal(original, a.Hash);
        Assert.False(result.AllRequiredAvailable);
    }

    [Fact]
    public async Task NotSyncedAndForbiddenFiles_AreNeverExportable_EvenIfPresentLocally()
    {
        var forbidden = _fx.AddCacheFile(TestFixture.RandomBytes(10, 10), "tex");
        var filtered = _fx.AddCacheFile(TestFixture.RandomBytes(10, 11), "scd");
        var c = TestFixture.Character([new(Player, forbidden, ["chara/a/b.tex"])], b =>
        {
            b.Forbidden.Add(forbidden);
            b.NotSynced.Add(new NotSyncedFile(new(Player, filtered, ["sound/a/b.scd"]), NotSyncedReason.PlayerSyncFilter));
        });

        var result = await _fx.Analyzer().AnalyzeAsync(c);

        Assert.Equal(AssetStatus.ForbiddenByServer, Assert.Single(result.Assets, a => a.Hash == forbidden).Status);
        Assert.Equal(AssetStatus.NotSynced, Assert.Single(result.Assets, a => a.Hash == filtered).Status);
        Assert.All(result.Assets, a => Assert.False(a.IsExportable || a.Include));
    }

    [Fact]
    public async Task NonPlayerFiles_AreNotPreselectedByDefault()
    {
        var pet = _fx.AddCacheFile(TestFixture.RandomBytes(10, 12), "tex");
        var result = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Pet, pet, ["chara/demihuman/d0001/x.tex"])]));
        var a = Assert.Single(result.Assets);
        Assert.True(a.IsExportable);
        Assert.False(a.Include);
    }

    [Fact]
    public async Task PlayerSyncUnavailable_NoLocators_EverythingMissing()
    {
        var hash = _fx.AddCacheFile(TestFixture.RandomBytes(10, 13), "tex");
        var analyzer = new Core.Analysis.AssetAnalyzer([new CacheFolderLocator(Path.Combine(_fx.Root, "does-not-exist"))]);
        var result = await analyzer.AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/a/b.tex"])]));
        Assert.Equal(AssetStatus.Missing, Assert.Single(result.Assets).Status);
    }

    [Fact]
    public async Task FailingLocator_DoesNotAbortAnalysis()
    {
        var hash = _fx.AddCacheFile(TestFixture.RandomBytes(10, 14), "tex");
        var throwing = new DelegateLocator("broken", (_, _) => throw new InvalidOperationException("reflection failed"));
        var result = await _fx.Analyzer(throwing).AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/a/b.tex"])]));
        Assert.Equal(AssetStatus.Available, Assert.Single(result.Assets).Status);
    }

    [Fact]
    public async Task PenumbraModDirectoryCopy_IsFoundThroughFileCacheCsv()
    {
        var content = TestFixture.RandomBytes(300, 15);
        var hash = TestFixture.Sha1(content);
        var modFile = Path.Combine(_fx.PenumbraFolder, "SomeMod", "files", "x.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(modFile)!);
        File.WriteAllBytes(modFile, content);
        var csv = Path.Combine(_fx.Root, "FileCache.csv");
        File.WriteAllText(csv, $"{hash.ToLowerInvariant()}|{{penumbra}}\\SomeMod\\files\\x.tex|0|300|-1\n");

        var locator = new FileCacheCsvLocator(FileCacheCsv.Read(csv), _fx.CacheFolder, _fx.PenumbraFolder);
        var result = await new Core.Analysis.AssetAnalyzer([locator]).AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/a/b.tex"])]));

        var a = Assert.Single(result.Assets);
        Assert.Equal(AssetStatus.Available, a.Status);
        Assert.Equal(AssetProvenance.PenumbraModDirectory, a.Provenance);
    }

    [Fact]
    public async Task Cancellation_StopsAnalysis()
    {
        var files = Enumerable.Range(0, 50)
            .Select(i => new CapturedFileReplacement(Player, _fx.AddCacheFile(TestFixture.RandomBytes(1000, 100 + i), "tex"), [$"chara/a/{i}.tex"]))
            .ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Analyzer().AnalyzeAsync(TestFixture.Character(files), token: cts.Token));
    }
}
