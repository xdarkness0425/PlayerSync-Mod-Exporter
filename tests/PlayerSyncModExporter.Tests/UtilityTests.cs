using System.Collections;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Locating;
using PlayerSyncModExporter.Core.Meta;
using PlayerSyncModExporter.Core.Model;

namespace PlayerSyncModExporter.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("Normal Name", "Normal Name")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("...", "export")]
    [InlineData("  trailing.  ", "trailing")]
    [InlineData("", "export")]
    public void SanitizeFileName(string input, string expected) => Assert.Equal(expected, PathSafety.SanitizeFileName(input));

    [Theory]
    [InlineData("chara/equipment/e0001/x.tex", true)]
    [InlineData("CHARA\\Equipment\\X.TEX", true)]
    [InlineData("../x.tex", false)]
    [InlineData("chara/../../x.tex", false)]
    [InlineData("/chara/x.tex", false)]
    [InlineData("c:/chara/x.tex", false)]
    [InlineData("chara//x.tex", false)]
    [InlineData("x.tex", false)]
    [InlineData("chara/noext", false)]
    [InlineData("chara/ü.tex", false)]
    public void GamePathValidation(string input, bool valid)
    {
        Assert.Equal(valid, PathSafety.TryNormalizeGamePath(input, out var normalized));
        if (valid)
            Assert.Equal(input.Replace('\\', '/').ToLowerInvariant(), normalized);
    }

    [Fact]
    public void CombineUnderRoot_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.True(PathSafety.TryCombineUnderRoot(root, "a/b.tex", out _));
        Assert.False(PathSafety.TryCombineUnderRoot(root, "../outside.tex", out _));
        Assert.False(PathSafety.TryCombineUnderRoot(root, "a/../../outside.tex", out _));
        Assert.False(PathSafety.TryCombineUnderRoot(root, Path.Combine(Path.GetTempPath(), "abs.tex"), out _));
        Assert.False(PathSafety.TryCombineUnderRoot(root + "2", "../root2-sibling", out _));
    }
}

public class FileCacheCsvTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void ParsesValidLines_IgnoresMalformed_AndRejectsTraversal()
    {
        var hash = new string('a', 40);
        var csv = Path.Combine(_fx.Root, "FileCache.csv");
        File.WriteAllLines(csv, [
            $"{hash}|{{cache}}\\{hash}.tex|638000000000000000|100|50",
            $"{hash}|{{penumbra}}\\Mod\\x.tex|638000000000000000",
            "tooshort|{cache}\\x.tex|1|1|1",
            $"{hash}|C:\\absolute\\x.tex|1|1|1",
            $"{hash}|{{cache}}\\..\\..\\escape.tex|1|1|1",
            "",
            "garbage",
        ]);

        var warnings = new List<string>();
        var records = FileCacheCsv.Read(csv, warnings);

        Assert.Equal(3, records.Count);
        Assert.Equal(3, warnings.Count);
        Assert.All(records, r => Assert.Equal(hash.ToUpperInvariant(), r.Hash));
        Assert.Equal(100, records[0].Size);
        Assert.Equal(-1, records[1].Size);

        var resolved = records.Select(r => FileCacheCsv.Resolve(r, _fx.CacheFolder, _fx.PenumbraFolder)).ToList();
        Assert.NotNull(resolved[0]);
        Assert.Equal(AssetProvenance.PenumbraModDirectory, resolved[1]!.Provenance);
        Assert.Null(resolved[2]);
    }

    [Fact]
    public void MissingCsv_YieldsNothing() => Assert.Empty(FileCacheCsv.Read(Path.Combine(_fx.Root, "nope.csv")));

    [Fact]
    public void ReadingCsv_DoesNotLockIt()
    {
        var csv = Path.Combine(_fx.Root, "FileCache.csv");
        File.WriteAllText(csv, "");
        using var writer = new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Empty(FileCacheCsv.Read(csv));
    }
}

public class StagingTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Cleanup_OnlyDeletesOwnedStagingDirectories()
    {
        using (var staging = StagingDirectory.Create(_fx.ExportRoot))
        {
            staging.MarkCommitted(); // simulate an abandoned staging dir that survived a crash
        }

        var impostor = Directory.CreateDirectory(Path.Combine(_fx.ExportRoot, StagingDirectory.Prefix + "notours")).FullName;
        var userDir = Directory.CreateDirectory(Path.Combine(_fx.ExportRoot, "My Export")).FullName;

        var removed = StagingDirectory.CleanupAbandoned(_fx.ExportRoot, TimeSpan.Zero);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(impostor));
        Assert.True(Directory.Exists(userDir));
    }

    [Fact]
    public void Dispose_RemovesUncommittedStaging()
    {
        string path;
        using (var staging = StagingDirectory.Create(_fx.ExportRoot))
            path = staging.Path;
        Assert.False(Directory.Exists(path));
    }
}

public class MetaCodecTests
{
    [Fact]
    public void Empty_IsEmpty() => Assert.True(MetaManipulationCodec.Inspect("").IsEmpty);

    [Fact]
    public void Garbage_IsNotDecodable()
    {
        var info = MetaManipulationCodec.Inspect("not base64!!");
        Assert.False(info.IsDecodable);
        Assert.NotNull(info.Error);
        Assert.False(new Version0MetaConverter().TryConvert("not base64!!", out _, out _));
    }

    [Fact]
    public void Version0_RoundTrips()
    {
        const string json = """[{"Type":"Eqp","Manipulation":{"Entry":0,"SetId":1,"Slot":"Body"}}]""";
        var b64 = MetaManipulationCodec.EncodeVersion0(json);
        Assert.Equal((byte)0, MetaManipulationCodec.Inspect(b64).Version);
        Assert.True(new Version0MetaConverter().TryConvert(b64, out var outJson, out _));
        Assert.Equal(1, MetaManipulationCodec.CountEntries(outJson));
    }

    [Fact]
    public void Version0_RejectsWrongShape()
        => Assert.False(new Version0MetaConverter().TryConvert(MetaManipulationCodec.EncodeVersion0("""[{"nope":1}]"""), out _, out _));

    [Fact]
    public void Chain_ReportsAllErrors()
    {
        var chain = new ChainedMetaConverter(new Version0MetaConverter(), new Version0MetaConverter());
        Assert.False(chain.TryConvert("x", out _, out var error));
        Assert.Contains("|", error);
    }
}

/// <summary>Fake types with the same member names as PlayerSync's MareAPI data classes.</summary>
public class PlayerSyncDataReaderTests
{
    private enum FakeObjectKind { Player = 0, MinionOrMount = 1, Companion = 2, Pet = 3 }

    private sealed class FakeFileReplacementData
    {
        public string FileSwapPath { get; set; } = string.Empty;
        public string[] GamePaths { get; set; } = [];
        public string Hash { get; set; } = string.Empty;
    }

    private sealed class FakeCharacterData
    {
        public Dictionary<FakeObjectKind, string> CustomizePlusData { get; set; } = new();
        public Dictionary<FakeObjectKind, List<FakeFileReplacementData>> FileReplacements { get; set; } = new();
        public Dictionary<FakeObjectKind, string> GlamourerData { get; set; } = new();
        public string ManipulationData { get; set; } = string.Empty;
        public string HonorificData { get; set; } = "should not be read";
    }

    private static FakeCharacterData Sample()
    {
        var h1 = new string('1', 40);
        var h2 = new string('2', 40);
        return new FakeCharacterData
        {
            GlamourerData = { [FakeObjectKind.Player] = "glam", [FakeObjectKind.Pet] = "petglam" },
            CustomizePlusData = { [FakeObjectKind.Player] = "cplus" },
            ManipulationData = "meta",
            FileReplacements =
            {
                [FakeObjectKind.Player] =
                [
                    new() { Hash = h1, GamePaths = ["chara/a.tex"] },
                    new() { Hash = h2, GamePaths = ["sound/a.scd"] },
                    new() { FileSwapPath = "chara/b.mdl", GamePaths = ["chara/c.mdl"] },
                    new() { Hash = h1, GamePaths = [] },
                ],
                [FakeObjectKind.Pet] = [new() { Hash = h1, GamePaths = ["chara/pet.tex"] }],
            },
        };
    }

    [Fact]
    public void ReadsCharacterData_AndDetectsFilteredFiles()
    {
        var received = Sample();
        var applied = (FakeCharacterData)PlayerSyncDataReader.JsonClone(received)!;
        applied.FileReplacements[FakeObjectKind.Player].RemoveAt(1); // PlayerSync's sound filter removed the .scd

        var c = PlayerSyncDataReader.Read(received, applied, new PlayerSyncDataReader.ReadOptions { Label = "X" });

        Assert.Equal("glam", c.GlamourerData);
        Assert.Equal("cplus", c.CustomizePlusData);
        Assert.Equal("meta", c.ManipulationData);
        Assert.Equal(2, c.Files.Count);
        Assert.Contains(c.Files, f => f.Kind == SyncObjectKind.Pet);
        Assert.Single(c.FileSwaps);
        var ns = Assert.Single(c.NotSyncedFiles);
        Assert.Equal("sound/a.scd", ns.File.GamePaths[0]);
        Assert.Contains(c.CaptureWarnings, w => w.Contains("no game paths"));
        // The source object is untouched.
        Assert.Equal(4, received.FileReplacements[FakeObjectKind.Player].Count);
    }

    [Fact]
    public void WithoutFilter_WarnsAndTreatsAllAsSynced()
    {
        var c = PlayerSyncDataReader.Read(Sample(), null, new PlayerSyncDataReader.ReadOptions { Label = "X" });
        Assert.Empty(c.NotSyncedFiles);
        Assert.Contains(c.CaptureWarnings, w => w.Contains("permission filter"));
    }

    [Fact]
    public void IncompatibleShape_IsReportedNotThrown()
    {
        var c = PlayerSyncDataReader.Read(new { Something = 1 }, null, new PlayerSyncDataReader.ReadOptions { Label = "X" });
        Assert.Empty(c.Files);
        Assert.Contains(c.CaptureWarnings, w => w.Contains("incompatible"));
    }

    [Fact]
    public void UnknownObjectKind_IsSkipped()
    {
        var data = new { FileReplacements = (IDictionary)new Dictionary<int, List<FakeFileReplacementData>> { [42] = [new() { Hash = new string('3', 40), GamePaths = ["a/b.tex"] }] } };
        var c = PlayerSyncDataReader.Read(data, null, new PlayerSyncDataReader.ReadOptions { Label = "X" });
        Assert.Empty(c.Files);
        Assert.Contains(c.CaptureWarnings, w => w.Contains("Unknown object kind"));
    }
}
