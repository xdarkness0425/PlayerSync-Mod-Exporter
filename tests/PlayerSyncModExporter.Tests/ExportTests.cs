using System.IO.Compression;
using System.Text.Json.Nodes;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Meta;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using PlayerSyncModExporter.Core.Safety;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

public class ExportTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<(Core.Analysis.AnalysisResult Analysis, string Tex, string Mdl)> TwoFileCharacter(Action<CapturedCharacterBuilder>? configure = null)
    {
        var tex = _fx.AddCacheFile(TestFixture.RandomBytes(10_000, 1), "tex");
        var mdl = _fx.AddCacheFile(TestFixture.RandomBytes(5_000, 2), "mdl");
        var c = TestFixture.Character([
            new(Player, tex, ["chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex", "chara/equipment/e0001/texture/v01_c0101e0001_top_d_copy.tex"]),
            new(Player, mdl, ["chara/equipment/e0001/model/c0101e0001_top.mdl"]),
        ], b =>
        {
            b.Swaps.Add(new CapturedFileSwap(Player, "chara/equipment/e0002/model/c0101e0002_top.mdl", ["chara/equipment/e0003/model/c0101e0003_top.mdl"]));
            configure?.Invoke(b);
        });
        return (await _fx.Analyzer().AnalyzeAsync(c), tex, mdl);
    }

    [Fact]
    public async Task CompleteExport_WritesValidPenumbraImportablePmp_McdfAndManifest()
    {
        var (analysis, tex, mdl) = await TwoFileCharacter();
        var before = _fx.SnapshotCache();

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));

        // PMP structure exactly as Penumbra's archive importer expects (root meta.json with a Name).
        using (var zip = ZipFile.OpenRead(result.PmpPath!))
        {
            var meta = JsonNode.Parse(zip.GetEntry("meta.json")!.Open())!;
            Assert.Equal(3, meta["FileVersion"]!.GetValue<int>());
            Assert.Equal("Test Character", meta["Name"]!.GetValue<string>());

            var def = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!;
            var files = def["Files"]!.AsObject();
            Assert.Equal(3, files.Count);
            Assert.Equal($"files\\{tex.ToLowerInvariant()}.tex", files["chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex"]!.GetValue<string>());
            Assert.Equal($"files\\{mdl.ToLowerInvariant()}.mdl", files["chara/equipment/e0001/model/c0101e0001_top.mdl"]!.GetValue<string>());
            Assert.Equal("chara/equipment/e0002/model/c0101e0002_top.mdl",
                def["FileSwaps"]!["chara/equipment/e0003/model/c0101e0003_top.mdl"]!.GetValue<string>());
            Assert.Empty(def["Manipulations"]!.AsArray());

            // Identical content referenced by two game paths is stored once.
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("files/")));
            Assert.NotNull(zip.GetEntry("export_manifest.json"));
        }

        var (mcdf, problems) = await McdfFormat.VerifyAsync(result.McdfPath!, CancellationToken.None);
        Assert.Empty(problems);
        Assert.Equal(2, mcdf.Files.Count);
        Assert.Equal(analysis.Character.GlamourerData, mcdf.GlamourerData);
        Assert.Single(mcdf.FileSwaps);

        var manifest = JsonNode.Parse(File.ReadAllText(result.ManifestPath))!;
        Assert.Equal(ExportManifest.SchemaId, manifest["schema"]!.GetValue<string>());
        Assert.Equal("Complete", manifest["completeness"]!["status"]!.GetValue<string>());
        Assert.Equal(2, manifest["summary"]!["includedFiles"]!.GetValue<int>());
        Assert.True(manifest["consent"]!["permissionConfirmed"]!.GetValue<bool>());
        Assert.Equal("Unknown", manifest["files"]![0]!["sharingStatus"]!.GetValue<string>());
        Assert.Equal("reconstructed", manifest["package"]!["origin"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(result.PackageDirectory, "appearance", "glamourer.b64.txt")));

        // The source cache is byte-for-byte and timestamp-for-timestamp unchanged, with no new files.
        Assert.Equal(before, _fx.SnapshotCache());
        Assert.Empty(Directory.GetDirectories(_fx.ExportRoot, StagingDirectory.Prefix + "*"));
    }

    [Fact]
    public async Task MissingAssets_ProducePartialPackage_WithReasons()
    {
        var present = _fx.AddCacheFile(TestFixture.RandomBytes(100, 3), "tex");
        var missing = TestFixture.Sha1(TestFixture.RandomBytes(100, 4));
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([
            new(Player, present, ["chara/a/b.tex"]),
            new(Player, missing, ["chara/a/c.tex"]),
        ]));

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));

        Assert.Equal(CompletenessStatus.Partial, result.Manifest.Completeness.Status);
        Assert.Contains(result.Manifest.Completeness.Reasons, r => r.Contains("missing"));
        var entry = Assert.Single(result.Manifest.Files, f => f.Hash == missing);
        Assert.False(entry.Included);
        Assert.Equal("Missing", entry.Status);
    }

    [Fact]
    public async Task UserExclusion_IsRecordedAsPartial()
    {
        var (analysis, tex, _) = await TwoFileCharacter();
        analysis.Assets.Single(a => a.Hash == tex).Include = false;
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        Assert.Equal(CompletenessStatus.Partial, result.Manifest.Completeness.Status);
        Assert.Equal(1, result.Manifest.Summary.ExcludedByUser);
    }

    [Fact]
    public async Task ExportWithoutConsent_IsRefused()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        var request = _fx.Request(analysis, configure: b => b.Consent = new ExportConsent { PermissionConfirmed = true });
        await Assert.ThrowsAsync<ConsentRequiredException>(() => new ExportPackager().ExportAsync(request));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.ExportRoot));
    }

    [Fact]
    public async Task NothingExportable_IsRefused()
    {
        var missing = TestFixture.Sha1([9, 9, 9]);
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Player, missing, ["chara/a/b.tex"])]));
        var e = await Assert.ThrowsAsync<ExportFailedException>(() => new ExportPackager().ExportAsync(_fx.Request(analysis)));
        Assert.Contains("Nothing to export", e.Message);
    }

    [Fact]
    public async Task SourceModifiedAfterAnalysis_FailsAndLeavesNothingBehind()
    {
        var (analysis, tex, _) = await TwoFileCharacter();
        File.WriteAllBytes(Path.Combine(_fx.CacheFolder, tex.ToLowerInvariant() + ".tex"), TestFixture.RandomBytes(10_000, 99));

        var e = await Assert.ThrowsAsync<ExportFailedException>(() => new ExportPackager().ExportAsync(_fx.Request(analysis)));
        Assert.Contains("changed after analysis", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_fx.ExportRoot));
    }

    [Fact]
    public async Task Cancellation_MidExport_CleansUpStaging()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<ExportProgress>(p =>
        {
            if (p.Stage == ExportStage.WritingPmp && p.BytesDone > 0)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportPackager().ExportAsync(_fx.Request(analysis), progress, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(_fx.ExportRoot));
    }

    [Fact]
    public async Task InsufficientDiskSpace_IsDetectedBeforeWriting()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        var packager = new ExportPackager(diskSpace: new FixedDiskSpace(1024));
        var e = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() => packager.ExportAsync(_fx.Request(analysis)));
        Assert.True(e.Required > e.Available);
        Assert.Empty(Directory.GetFileSystemEntries(_fx.ExportRoot));
    }

    [Fact]
    public async Task LargePackage_IsStreamedAndVerified()
    {
        // 48 MiB payload: exercised through the bounded 1 MiB copy buffer, not loaded into memory at once.
        var big = new byte[48 * 1024 * 1024];
        new Random(42).NextBytes(big);
        var hash = _fx.AddCacheFile(big, "tex");
        big = null!;
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/a/huge.tex"])]));

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));

        Assert.Equal(48L * 1024 * 1024, result.Manifest.Summary.IncludedBytes);
        Assert.Contains(result.VerificationNotes, n => n.StartsWith("PMP verified"));
        Assert.Contains(result.VerificationNotes, n => n.StartsWith("MCDF verified"));
    }

    [Fact]
    public async Task ExistingFolder_FailPolicy_Throws_AndKeepsExisting()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        var existing = Directory.CreateDirectory(Path.Combine(_fx.ExportRoot, "Test Character")).FullName;
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "user data");

        await Assert.ThrowsAsync<ExportFailedException>(() => new ExportPackager().ExportAsync(_fx.Request(analysis)));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public async Task ExistingFolder_NumberedCopy_CreatesNewFolder()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        Directory.CreateDirectory(Path.Combine(_fx.ExportRoot, "Test Character"));
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: b => b.Policy = ExistingPackagePolicy.CreateNumberedCopy));
        Assert.EndsWith("Test Character (2)", result.PackageDirectory);
    }

    [Fact]
    public async Task ReplaceExisting_OnlyReplacesPreviousExports()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        var first = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        var second = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: b => b.Policy = ExistingPackagePolicy.ReplaceExisting));
        Assert.Equal(first.PackageDirectory, second.PackageDirectory);
        Assert.Single(Directory.GetDirectories(_fx.ExportRoot));

        var foreign = Directory.CreateDirectory(Path.Combine(_fx.ExportRoot, "Foreign")).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");
        await Assert.ThrowsAsync<ExportFailedException>(() => new ExportPackager().ExportAsync(
            _fx.Request(analysis, "Foreign", b => b.Policy = ExistingPackagePolicy.ReplaceExisting)));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
    }

    [Fact]
    public async Task UnsafePackageName_IsSanitized()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, "../../evil:name"));
        Assert.True(PathSafety.IsUnderRoot(_fx.ExportRoot, result.PackageDirectory));
        Assert.DoesNotContain(":", Path.GetFileName(result.PackageDirectory));
    }

    [Fact]
    public async Task CompressedAlternates_AreExportedButFlagged()
    {
        var original = TestFixture.Sha1(TestFixture.RandomBytes(4000, 8));
        var alt = _fx.AddCacheFile(TestFixture.Tex(64, 64, seed: 9), "tex");
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Player, original, ["chara/a/b.tex"])], b => b.Redirects[original] = alt));

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));

        Assert.Equal(CompletenessStatus.Partial, result.Manifest.Completeness.Status);
        var f = Assert.Single(result.Manifest.Files);
        Assert.False(f.IsOriginalContent);
        Assert.Equal(alt, f.ContentHash);
        Assert.Equal($"files/{alt.ToLowerInvariant()}.tex", f.ArchivePath);
    }

    [Fact]
    public async Task Version0Meta_IsEmbedded()
    {
        const string manips = """[{"Type":"Rsp","Manipulation":{"Entry":1.1,"SubRace":"Midlander","Attribute":"MaleMaxSize"}}]""";
        var (analysis, _, _) = await TwoFileCharacter(b => b.Manipulations = MetaManipulationCodec.EncodeVersion0(manips));

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));

        Assert.True(result.Manifest.Meta.EmbeddedInPmp);
        Assert.Equal(1, result.Manifest.Meta.EmbeddedEntries);
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        var def = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!;
        Assert.Equal("Rsp", def["Manipulations"]![0]!["Type"]!.GetValue<string>());
        Assert.Equal(CompletenessStatus.Complete, result.Manifest.Completeness.Status);
    }

    [Fact]
    public async Task BinaryMeta_WithoutPenumbra_IsPreservedButNotEmbedded()
    {
        // Version 1 header ("META0001") as written by current Penumbra; the built-in converter cannot decode it.
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
        {
            gz.WriteByte(1);
            gz.Write("META0001"u8);
            gz.Write(new byte[28]);
        }

        var b64 = Convert.ToBase64String(ms.ToArray());
        var (analysis, _, _) = await TwoFileCharacter(b => b.Manipulations = b64);

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));

        Assert.False(result.Manifest.Meta.EmbeddedInPmp);
        Assert.Equal(1, result.Manifest.Meta.FormatVersion);
        Assert.Equal(CompletenessStatus.Partial, result.Manifest.Completeness.Status);
        Assert.Equal(b64, File.ReadAllText(Path.Combine(result.PackageDirectory, "appearance", "penumbra-meta.b64.txt")));
        var (mcdf, _) = await McdfFormat.VerifyAsync(result.McdfPath!, CancellationToken.None);
        Assert.Equal(b64, mcdf.ManipulationData);
    }

    [Fact]
    public async Task NonPlayerObjects_AreOnlyIncludedWhenRequested_AndPlayerWinsConflicts()
    {
        var player = _fx.AddCacheFile(TestFixture.RandomBytes(100, 20), "tex");
        var minion = _fx.AddCacheFile(TestFixture.RandomBytes(100, 21), "tex");
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([
            new(Player, player, ["chara/shared/x.tex"]),
            new(MinionOrMount, minion, ["chara/shared/x.tex", "chara/monster/m1/y.tex"]),
        ]));
        foreach (var a in analysis.Assets)
            a.Include = true;

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: b => b.NonPlayer = true));

        using var zip = ZipFile.OpenRead(result.PmpPath!);
        var files = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!["Files"]!.AsObject();
        Assert.Equal($"files\\{player.ToLowerInvariant()}.tex", files["chara/shared/x.tex"]!.GetValue<string>());
        Assert.Equal($"files\\{minion.ToLowerInvariant()}.tex", files["chara/monster/m1/y.tex"]!.GetValue<string>());
        Assert.Contains(result.Manifest.Warnings, w => w.Contains("chara/shared/x.tex"));

        // MCDF only carries the player object.
        var (mcdf, _) = await McdfFormat.VerifyAsync(result.McdfPath!, CancellationToken.None);
        Assert.Equal(player, Assert.Single(mcdf.Files).Hash);
    }

    [Fact]
    public async Task PmpOnly_And_McdfOnly_Work()
    {
        var (analysis, _, _) = await TwoFileCharacter();
        var pmp = await new ExportPackager().ExportAsync(_fx.Request(analysis, "A", b => b.Mcdf = false));
        Assert.Null(pmp.McdfPath);
        Assert.True(File.Exists(pmp.PmpPath));
        var mcdf = await new ExportPackager().ExportAsync(_fx.Request(analysis, "B", b => b.Pmp = false));
        Assert.Null(mcdf.PmpPath);
        Assert.True(File.Exists(mcdf.McdfPath));
    }

    private sealed class FixedDiskSpace(long bytes) : IDiskSpaceProbe
    {
        public long? GetAvailableBytes(string path) => bytes;
    }
}

public sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
