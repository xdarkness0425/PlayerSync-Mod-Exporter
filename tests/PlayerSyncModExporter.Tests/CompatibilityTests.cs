using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using K4os.Compression.LZ4.Legacy;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Packaging;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

/// <summary>
/// Reads exported packages the way the consuming programs do, independently of the exporter's own verifier.
/// </summary>
public class CompatibilityTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // Mirrors of PlayerSync's types (PlayerSync/Services/CharaData/Models/MareCharaFileData.cs, MIT).
    private sealed record MareCharaFileData
    {
        public string Description { get; set; } = string.Empty;
        public string GlamourerData { get; set; } = string.Empty;
        public string CustomizePlusData { get; set; } = string.Empty;
        public string ManipulationData { get; set; } = string.Empty;
        public List<FileData> Files { get; set; } = [];
        public List<FileSwap> FileSwaps { get; set; } = [];
    }

    private sealed record FileSwap(IEnumerable<string> GamePaths, string FileSwapPath);

    private sealed record FileData(IEnumerable<string> GamePaths, int Length, string Hash);

    [Fact]
    public async Task Mcdf_IsReadableWithPlayerSyncsReaderLogic()
    {
        var a = TestFixture.RandomBytes(3000, 1);
        var b = TestFixture.RandomBytes(7000, 2);
        var ha = _fx.AddCacheFile(a, "tex");
        var hb = _fx.AddCacheFile(b, "mdl");
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character(
            [new(Player, ha, ["chara/x/a.tex"]), new(Player, hb, ["chara/x/b.mdl"])],
            c => c.Swaps.Add(new(Player, "chara/x/c.mdl", ["chara/x/d.mdl"]))));
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: r => r.Pmp = false));

        // Same calls as CharaDataFileHandler.LoadCharaFileHeader / MareCharaFileHeader.FromBinaryReader / McdfExtractFiles.
        using var lz4 = new LZ4Stream(File.OpenRead(result.McdfPath!), LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4);
        Assert.Equal("MCDF", new string(reader.ReadChars(4)));
        Assert.Equal(1, reader.ReadByte());
        var length = reader.ReadInt32();
        var data = JsonSerializer.Deserialize<MareCharaFileData>(Encoding.UTF8.GetString(reader.ReadBytes(length)))!;

        Assert.Equal(2, data.Files.Count);
        Assert.Equal("chara/x/c.mdl", Assert.Single(data.FileSwaps).FileSwapPath);
        var contents = new Dictionary<string, byte[]> { [ha] = a, [hb] = b };
        foreach (var file in data.Files)
            Assert.Equal(contents[file.Hash], reader.ReadBytes(file.Length));
        Assert.Equal(-1, lz4.ReadByte());
    }

    [Fact]
    public async Task Pmp_SatisfiesPenumbraArchiveImportRules()
    {
        var hash = _fx.AddCacheFile(TestFixture.RandomBytes(500, 3), "tex");
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Player, hash, ["chara/x/a.tex"])]));
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, "Name with ünïcödé", r => r.Mcdf = false));

        using var zip = ZipFile.OpenRead(result.PmpPath!);
        // Penumbra/Import/TexToolsImporter.Archives.cs FindArchiveModMeta: meta.json must be at the root (or one folder deep).
        Assert.Contains(zip.Entries, e => e.FullName == "meta.json");
        // HandleRegularArchive: the meta must have a non-empty Name.
        var meta = JsonNode.Parse(zip.GetEntry("meta.json")!.Open())!;
        Assert.False(string.IsNullOrWhiteSpace(meta["Name"]!.GetValue<string>()));
        // No symbolic links, absolute paths or traversal; every entry is a plain file.
        Assert.All(zip.Entries, e =>
        {
            Assert.False(e.FullName.StartsWith('/') || e.FullName.Contains("..") || e.FullName.Contains(':'));
            Assert.False(e.FullName.EndsWith('/'));
        });
        // SubMod.LoadDataContainer: Files values are relative paths that exist in the extracted mod folder.
        var files = JsonNode.Parse(zip.GetEntry("default_mod.json")!.Open())!["Files"]!.AsObject();
        foreach (var (_, rel) in files)
            Assert.NotNull(zip.GetEntry(rel!.GetValue<string>().Replace('\\', '/')));

        // Extracting the package (as Penumbra does) yields files whose hashes match the manifest.
        var extract = Path.Combine(_fx.Root, "extract");
        zip.ExtractToDirectory(extract);
        var manifest = JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(Path.Combine(extract, PmpWriter.ManifestEntry)), ExportManifest.JsonOptions)!;
        foreach (var f in manifest.Files.Where(f => f.Included))
            Assert.Equal(f.ContentHash, TestFixture.Sha1(File.ReadAllBytes(Path.Combine(extract, f.ArchivePath!))));
        Assert.True(PathSafety.IsUnderRoot(_fx.ExportRoot, result.PackageDirectory));
    }
}
