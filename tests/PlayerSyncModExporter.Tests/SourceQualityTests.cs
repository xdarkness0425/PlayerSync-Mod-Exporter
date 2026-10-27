using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using PlayerSyncModExporter.Core.SourceQuality;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

public class TextureSourceSettingTests
{
    [Theory]
    [InlineData(null, false, TextureSourceMode.AlwaysCompressed)] // never set: PlayerSync's default
    [InlineData(0, false, TextureSourceMode.AlwaysSourceQuality)]
    [InlineData(1, false, TextureSourceMode.CompressedNewDownloads)]
    [InlineData(3, false, TextureSourceMode.AlwaysCompressed)]
    [InlineData(3, true, TextureSourceMode.AlwaysSourceQuality)] // Override UIDs list wins, like ComputeCompressedAlternateUsage
    [InlineData(1, true, TextureSourceMode.AlwaysSourceQuality)]
    public void EffectiveMode_FollowsPlayerSyncRules(int? raw, bool pairOverride, TextureSourceMode expected)
        => Assert.Equal(expected, TextureSourceSetting.FromPlayerSync(raw, pairOverride).Effective);

    [Fact]
    public void UnknownValue_IsReportedAsUnknown_NotGuessed()
    {
        var s = TextureSourceSetting.FromPlayerSync(2, pairOverride: false);
        Assert.Null(s.Effective);
        Assert.StartsWith("Unknown", s.Describe(), StringComparison.Ordinal);
        Assert.Null(TextureSourceSetting.Unknown.Effective);
    }

    [Fact]
    public void Descriptions_DistinguishEveryMode()
    {
        var all = new[]
        {
            TextureSourceSetting.FromPlayerSync(0, false), TextureSourceSetting.FromPlayerSync(1, false),
            TextureSourceSetting.FromPlayerSync(3, false), TextureSourceSetting.FromPlayerSync(3, true), TextureSourceSetting.Unknown,
        }.Select(s => s.Describe()).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains("Override UIDs", all[3]);
    }
}

public class SourceQualityDownloadResultTests
{
    // The exact shape PlayerSync's SourceQualityExportService.GetStatus returns (camelCase, enum names).
    private const string Status = """
        {"state":"Completed","error":null,"files":[
          {"hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","gamePath":"a.tex","status":"Downloaded","path":"C:\\x\\a.tex","size":10,"sha1Verified":true,"message":null},
          {"hash":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","gamePath":"b.tex","status":"Forbidden","path":null,"size":0,"sha1Verified":false,"message":"The file server forbids this file."},
          {"hash":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC","gamePath":"c.tex","status":"SomethingNew","path":null,"size":0,"sha1Verified":false,"message":null},
          {"gamePath":"no hash"}
        ]}
        """;

    [Fact]
    public void Parse_ReadsPlayerSyncsStatus_AndTreatsUnknownStatusesAsUnknown()
    {
        var r = SourceQualityDownloadResult.Parse(Status);
        Assert.Equal(("Completed", 3, true), (r.State, r.Requested, r.IsFinished));
        var a = r.Find("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")!;
        Assert.Equal((StagedOriginalStatus.Downloaded, "C:\\x\\a.tex", 10L, true, true), (a.Status, a.Path, a.Size, a.Sha1Verified, a.IsAvailable));
        Assert.Equal(StagedOriginalStatus.Forbidden, r.Find("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")!.Status);
        Assert.Equal(StagedOriginalStatus.Unknown, r.Find("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")!.Status);
        Assert.Equal("Originals requested from PlayerSync: 3 (1 downloaded, 1 forbidden by the server, 1 could not be retrieved).", r.Describe());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Parse_MalformedStatus_IsAFailureNotAnException(string json)
    {
        var r = SourceQualityDownloadResult.Parse(json);
        Assert.Equal("Failed", r.State);
        Assert.Empty(r.Files);
    }

    [Fact]
    public void Running_IsNotFinished_AndCountsProgress()
    {
        var r = SourceQualityDownloadResult.Parse("""{"state":"Running","files":[{"hash":"A","status":"Pending"},{"hash":"B","status":"Downloaded"}]}""");
        Assert.False(r.IsFinished);
        Assert.Equal((1, 2), (r.Finished, r.Requested));
    }
}

public class OriginalsStagingTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Dispose_DeletesOnlyItsOwnFolder_AndCleanupLeavesForeignFoldersAlone()
    {
        var foreign = Directory.CreateDirectory(Path.Combine(_fx.ExportRoot, OriginalsStaging.Prefix + "not-ours")).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");
        var abandoned = OriginalsStaging.Create(_fx.ExportRoot).Path; // simulates a crash: never disposed
        var cache = _fx.SnapshotCache();

        var staging = OriginalsStaging.Create(_fx.ExportRoot);
        File.WriteAllText(Path.Combine(staging.Path, "a.tex"), "x");
        Assert.True(OriginalsStaging.IsOwned(staging.Path));
        staging.Dispose();
        Assert.False(Directory.Exists(staging.Path));

        OriginalsStaging.CleanupAbandoned(_fx.ExportRoot);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Assert.Equal(cache, _fx.SnapshotCache());
    }
}

public class SourceQualityDownloadTests : IDisposable
{
    private const string Diffuse = "chara/equipment/e6001/texture/v01_c0201e6001_top_d.tex";
    private const string Normal = "chara/equipment/e6001/texture/v01_c0201e6001_top_n.tex";
    private const string Mask = "chara/equipment/e6001/texture/v01_c0201e6001_top_m.tex";
    private const string Spec = "chara/equipment/e6001/texture/v01_c0201e6001_top_s.tex";
    private const string Sound = "sound/foot/fs_test.scd";

    private static readonly TextureSourceSetting Compressed = TextureSourceSetting.FromPlayerSync(3, false);

    private readonly TestFixture _fx = new();
    private readonly OriginalsStaging _staging;

    public SourceQualityDownloadTests() => _staging = OriginalsStaging.Create(_fx.ExportRoot);

    public void Dispose()
    {
        _staging.Dispose();
        _fx.Dispose();
    }

    private Task<AnalysisResult> Analyze(CapturedCharacter c, SourceQualityDownloadResult? download = null)
    {
        IAssetLocator[] extra = download == null ? [] : [new StagedOriginalsLocator(_staging.Path, download)];
        return new AssetAnalyzer([.. extra, _fx.Locator]).AnalyzeAsync(c, new AssetAnalyzerOptions());
    }

    /// <summary>A texture PlayerSync replaced with its compressed substitute; the original bytes exist only "on the server".</summary>
    private (byte[] Original, string OriginalHash, string AltHash) CompressedTexture(int seed)
    {
        var original = TestFixture.Tex(512, 512, seed: seed);
        var alt = _fx.AddCacheFile(TestFixture.Tex(256, 256, seed: seed + 100), "tex");
        return (original, TestFixture.Sha1(original), alt);
    }

    /// <summary>What PlayerSync's export job does for a Downloaded file: the decoded original in the staging folder.</summary>
    private StagedOriginal Staged(byte[] bytes, string hash)
    {
        var path = Path.Combine(_staging.Path, hash.ToLowerInvariant() + ".tex");
        File.WriteAllBytes(path, bytes);
        return new StagedOriginal { Hash = hash, Status = StagedOriginalStatus.Downloaded, Path = path, Size = bytes.Length, Sha1Verified = true };
    }

    private static SourceQualityDownloadResult Completed(params StagedOriginal[] files) => new() { State = "Completed", Files = files };

    [Fact]
    public async Task Plan_RequestsOnlyCompressedAndInvalidSubstitutes_NeverFilteredOrForbiddenFiles()
    {
        var ok = _fx.AddCacheFile(TestFixture.Tex(64, 64, seed: 1), "tex");
        var (_, compressed, alt) = CompressedTexture(2);
        var broken = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 3));
        var brokenAlt = _fx.AddCacheFile(TestFixture.RandomBytes(3000, 3), "tex");
        var forbidden = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 4));
        var forbiddenAlt = _fx.AddCacheFile(TestFixture.Tex(256, 256, seed: 104), "tex");
        var filtered = TestFixture.Sha1(TestFixture.RandomBytes(100, 5));
        var missing = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 6));

        var analysis = await Analyze(TestFixture.Character(
            [new(Player, ok, [Diffuse]), new(Player, compressed, [Normal]), new(Player, broken, [Mask]), new(Player, forbidden, [Spec]),
             new(Player, missing, ["chara/equipment/e6001/texture/v01_c0201e6001_top_x.tex"])],
            b =>
            {
                b.Redirects[compressed] = alt;
                b.Redirects[broken] = brokenAlt;
                b.Redirects[forbidden] = forbiddenAlt;
                b.Forbidden.Add(forbidden);
                b.NotSynced.Add(new NotSyncedFile(new(Player, filtered, [Sound]), NotSyncedReason.PlayerSyncFilter));
            }));

        var plan = SourceQualityRequestPlan.From(analysis);
        Assert.Equal(new[] { broken, compressed }.Order(), plan.Hashes.Order());
    }

    [Fact]
    public async Task NothingCompressed_NoRequestIsNeeded()
    {
        var ok = _fx.AddCacheFile(TestFixture.Tex(64, 64, seed: 1), "tex");
        Assert.False(SourceQualityRequestPlan.From(await Analyze(TestFixture.Character([new(Player, ok, [Diffuse])]))).IsNeeded);
    }

    [Fact]
    public async Task SourceQualityOff_NoDownload_TheSubstituteIsFlagged_AndTheManifestSaysWhy()
    {
        var (_, hash, alt) = CompressedTexture(10);
        var analysis = await Analyze(TestFixture.Character([new(Player, hash, [Diffuse])], b =>
        {
            b.Redirects[hash] = alt;
            b.TextureSource = Compressed;
        }));

        var m = (await new ExportPackager().ExportAsync(_fx.Request(analysis))).Manifest;
        Assert.Equal(("AlwaysCompressed", "AlwaysCompressed", false, null), (m.TextureSource.EffectiveMode, m.TextureSource.GlobalMode, m.TextureSource.PairOverride, m.TextureSource.Download));
        Assert.Equal(CompletenessStatus.Partial, m.Completeness.Status);
        Assert.Contains(m.Completeness.Reasons, r => r.Contains("download the originals for an export", StringComparison.Ordinal));
        Assert.Equal("CompressedAlternate", m.Files.Single().Representation);
    }

    [Fact]
    public async Task DownloadedOriginal_IsExportedVerifiedAgainstItsOwnHash_AndTheCacheIsUntouched()
    {
        var (original, hash, alt) = CompressedTexture(11);
        var cacheBefore = _fx.SnapshotCache();
        var download = Completed(Staged(original, hash));
        var analysis = await Analyze(TestFixture.Character([new(Player, hash, [Diffuse])], b =>
        {
            b.Redirects[hash] = alt; // normal synchronization keeps using the substitute
            b.TextureSource = Compressed;
            b.Download = download;
        }), download);

        var a = Assert.Single(analysis.Assets);
        Assert.Equal((AssetStatus.Available, ContentRepresentation.Original, AssetProvenance.SourceQualityDownload, hash),
            (a.Status, a.Representation, a.Provenance, a.ContentHash));
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        var m = result.Manifest;
        Assert.Equal(CompletenessStatus.Complete, m.Completeness.Status);
        var f = m.Files.Single();
        Assert.Equal(("Original", "hash"), (f.Representation, f.VerifiedAgainst));
        Assert.Equal(("Completed", 1, 1, 0), (m.TextureSource.Download!.State, m.TextureSource.Download.OriginalsRequested, m.TextureSource.Download.OriginalsDownloaded, m.TextureSource.Download.NotRetrieved.Count));
        using var zip = System.IO.Compression.ZipFile.OpenRead(result.PmpPath!);
        using var s = zip.GetEntry(f.ArchivePath!)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(original, ms.ToArray());
        Assert.Equal(cacheBefore, _fx.SnapshotCache());
    }

    [Fact]
    public async Task StagedFileWithWrongBytes_IsRejected_AndTheSubstituteIsUsedAndFlagged()
    {
        var (_, hash, alt) = CompressedTexture(13);
        var download = Completed(Staged(TestFixture.Tex(512, 512, seed: 99), hash));
        var analysis = await Analyze(TestFixture.Character([new(Player, hash, [Diffuse])], b =>
        {
            b.Redirects[hash] = alt;
            b.Download = download;
        }), download);

        var a = Assert.Single(analysis.Assets);
        Assert.Equal((ContentRepresentation.CompressedAlternate, alt), (a.Representation, a.ContentHash));
        var m = (await new ExportPackager().ExportAsync(_fx.Request(analysis))).Manifest;
        Assert.Equal("compressedAlternateHash", m.Files.Single().VerifiedAgainst);
        Assert.Equal(CompletenessStatus.Partial, m.Completeness.Status);
    }

    [Fact]
    public void OnlyDownloadedFilesInsideTheStagingFolder_AreOffered()
    {
        var (original, hash, _) = CompressedTexture(14);
        var outside = Path.Combine(_fx.Root, hash.ToLowerInvariant() + ".tex");
        File.WriteAllBytes(outside, original);
        var cachedOriginal = new StagedOriginal { Hash = hash, Status = StagedOriginalStatus.CachedOriginal, Path = outside };
        var downloadedOutside = cachedOriginal with { Status = StagedOriginalStatus.Downloaded };

        Assert.Empty(new StagedOriginalsLocator(_staging.Path, Completed(cachedOriginal)).Locate(hash, "tex"));
        Assert.Empty(new StagedOriginalsLocator(_staging.Path, Completed(downloadedOutside)).Locate(hash, "tex"));
        Assert.Empty(new StagedOriginalsLocator(_staging.Path, Completed()).Locate(hash, "tex"));
    }

    [Fact]
    public async Task OriginalsPlayerSyncCouldNotProvide_StayCompressed_WithTheReason_AndTheRestIsMixed()
    {
        var (originalA, a, altA) = CompressedTexture(15);
        var (_, b, altB) = CompressedTexture(16);
        var (_, c, altC) = CompressedTexture(17);
        var download = Completed(
            Staged(originalA, a),
            new StagedOriginal { Hash = b, Status = StagedOriginalStatus.Forbidden, Message = "The file server forbids this file." },
            new StagedOriginal { Hash = c, Status = StagedOriginalStatus.NotOnServer, Message = "The file server no longer has the original file." });
        var analysis = await Analyze(TestFixture.Character([new(Player, a, [Diffuse]), new(Player, b, [Normal]), new(Player, c, [Mask])], x =>
        {
            x.Redirects[a] = altA;
            x.Redirects[b] = altB;
            x.Redirects[c] = altC;
            x.Download = download;
        }), download);

        var byHash = analysis.Assets.ToDictionary(x => x.Hash);
        Assert.Equal(ContentRepresentation.Original, byHash[a].Representation);
        Assert.Equal(ContentRepresentation.CompressedAlternate, byHash[b].Representation);
        Assert.Contains("could not provide the original (Forbidden", byHash[b].Note);
        Assert.Contains("NotOnServer", byHash[c].Note);

        var m = (await new ExportPackager().ExportAsync(_fx.Request(analysis))).Manifest;
        Assert.Equal(CompletenessStatus.Partial, m.Completeness.Status);
        Assert.Equal((2, 3), (m.Summary.CompressedAlternatesIncluded, m.Summary.IncludedFiles));
        Assert.Equal(new[] { "Forbidden", "NotOnServer" }, m.TextureSource.Download!.NotRetrieved.Select(n => n.Status).Order());
        Assert.Contains(m.Completeness.Reasons, r => r.Contains("notRetrieved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OriginalAlreadyInTheCache_WinsWithoutADownload_EvenWithAStaleRedirect()
    {
        var (original, hash, alt) = CompressedTexture(18);
        _fx.AddCacheFile(original, "tex");
        var a = Assert.Single((await Analyze(TestFixture.Character([new(Player, hash, [Diffuse])], b => b.Redirects[hash] = alt))).Assets);
        Assert.Equal((ContentRepresentation.Original, AssetProvenance.PlayerSyncCache), (a.Representation, a.Provenance));
    }
}
