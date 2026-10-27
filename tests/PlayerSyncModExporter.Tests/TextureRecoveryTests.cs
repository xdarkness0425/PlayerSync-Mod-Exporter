using System.IO.Compression;
using K4os.Compression.LZ4.Legacy;
using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Metadata;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using PlayerSyncModExporter.Core.Textures;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

public class TexFileValidatorTests
{
    [Theory]
    [InlineData(0x6432u, 256, 128, 9, "BC7")]
    [InlineData(0x3420u, 64, 64, 7, "BC1")]
    [InlineData(0x3431u, 4, 4, 3, "BC3")]
    [InlineData(0x1450u, 32, 16, 6, "B8G8R8A8")]
    [InlineData(0x6120u, 1, 1, 1, "BC4")]
    public void ValidTextures_ReportFormatDimensionsAndMips(uint format, int w, int h, int mips, string name)
    {
        var tex = TestFixture.Tex(w, h, format, mips);
        var check = TexFileValidator.Check(tex, tex.Length);
        Assert.True(check.Valid, check.Problem);
        Assert.Equal((name, w, h, mips, 1), (check.Format, check.Width, check.Height, check.MipLevels, check.ArraySize));
    }

    [Fact]
    public void TruncatedHeader_IsInvalid()
    {
        var tex = TestFixture.Tex(64, 64)[..40];
        Assert.Contains("shorter than", TexFileValidator.Check(tex, tex.Length).Problem);
    }

    [Fact]
    public void TruncatedPixelData_IsInvalid()
    {
        var tex = TestFixture.Tex(64, 64, truncateBy: 1);
        var check = TexFileValidator.Check(tex, tex.Length);
        Assert.False(check.Valid);
        Assert.Contains("needs 4096 bytes", check.Problem);
    }

    [Fact]
    public void HeaderClaimingHugeSurface_IsRejectedWithoutAllocating()
    {
        var tex = TestFixture.Tex(64, 64);
        BitConverter.GetBytes((ushort)8192).CopyTo(tex, 8);
        BitConverter.GetBytes((ushort)8192).CopyTo(tex, 10);
        var check = TexFileValidator.Check(tex, tex.Length);
        Assert.False(check.Valid);
        Assert.Contains("needs 67108864 bytes", check.Problem);
    }

    [Theory]
    [InlineData(4, new byte[] { 0x99, 0x99, 0, 0 }, "Unknown texture format")]
    [InlineData(8, new byte[] { 0, 0 }, "Implausible dimensions")]
    [InlineData(14, new byte[] { 0 }, "Implausible mip level count")]
    [InlineData(14, new byte[] { 14 }, "Implausible mip level count")]
    [InlineData(28, new byte[] { 10, 0, 0, 0 }, "inside the header")]
    [InlineData(28, new byte[] { 0xFF, 0xFF, 0, 0 }, "past the end")]
    public void InvalidHeaders_AreRejected(int offset, byte[] patch, string problem)
    {
        var tex = TestFixture.Tex(64, 64);
        patch.CopyTo(tex, offset);
        Assert.Contains(problem, TexFileValidator.Check(tex, tex.Length).Problem);
    }

    [Fact]
    public void RandomBytes_AreNotATexture()
    {
        var bytes = TestFixture.RandomBytes(5000, 1);
        Assert.False(TexFileValidator.Check(bytes, bytes.Length).Valid);
    }
}

public class TextureRecoveryTests : IDisposable
{
    private const string Diffuse = "chara/equipment/e6001/texture/v01_c0201e6001_top_d.tex";
    private const string Normal = "chara/equipment/e6001/texture/v01_c0201e6001_top_n.tex";
    private const string Mask = "chara/equipment/e6001/texture/v01_c0201e6001_top_m.tex";
    private const string Spec = "chara/equipment/e6001/texture/v01_c0201e6001_top_s.tex";

    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private Task<AnalysisResult> Analyze(CapturedCharacter c, ModLibraryIndex? library = null)
        => _fx.Analyzer().AnalyzeAsync(c, new AssetAnalyzerOptions { ModLibrary = library });

    [Fact]
    public async Task ValidCompressedAlternate_IsPreservedByteForByte_AndVerifiedAgainstTheAlternateHash()
    {
        var original = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 1));
        var altBytes = TestFixture.Tex(256, 256, mips: 9, seed: 2);
        var alt = _fx.AddCacheFile(altBytes, "tex");
        var analysis = await Analyze(TestFixture.Character([new(Player, original, [Diffuse])], b => b.Redirects[original] = alt));

        var a = Assert.Single(analysis.Assets);
        Assert.Equal((AssetStatus.Available, ContentRepresentation.CompressedAlternate), (a.Status, a.Representation));
        Assert.True(a.Texture!.Valid);
        Assert.Equal(("BC7", 256, 9), (a.Texture.Format, a.Texture.Width, a.Texture.MipLevels));

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        var f = Assert.Single(result.Manifest.Files);
        Assert.Equal(("CompressedAlternate", "compressedAlternateHash", alt), (f.Representation, f.VerifiedAgainst, f.ContentHash));
        Assert.Equal(original, f.Hash);
        Assert.Equal(CompletenessStatus.Partial, result.Manifest.Completeness.Status);
        Assert.Contains(result.Manifest.Completeness.Reasons, r => r.Contains("cannot be reconstructed", StringComparison.Ordinal));
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        Assert.Equal(altBytes, ReadAll(zip, f.ArchivePath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrTruncatedCompressedAlternate_IsNotExported(bool truncated)
    {
        var original = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 1));
        var alt = _fx.AddCacheFile(truncated ? TestFixture.Tex(256, 256, seed: 3, truncateBy: 100) : TestFixture.RandomBytes(4000, 3), "tex");
        var analysis = await Analyze(TestFixture.Character([new(Player, original, [Diffuse])], b => b.Redirects[original] = alt));

        var a = Assert.Single(analysis.Assets);
        Assert.Equal(AssetStatus.InvalidContent, a.Status);
        Assert.False(a.IsExportable);
        Assert.Contains("not a valid texture", a.Note);
        await Assert.ThrowsAsync<ExportFailedException>(() => new ExportPackager().ExportAsync(_fx.Request(analysis)));
    }

    [Fact]
    public async Task OriginalMissingFromPlayerSyncIndex_IsRecoveredFromInstalledModByGamePath()
    {
        var originalBytes = TestFixture.Tex(512, 512, mips: 10, seed: 4);
        var original = TestFixture.Sha1(originalBytes);
        var alt = _fx.AddCacheFile(TestFixture.Tex(256, 256, seed: 5), "tex");
        new ModBuilder(_fx.PenumbraFolder, "Cool Top").Meta("Cool Top", "Author A").DefaultFile(Diffuse, "tex/d.tex", originalBytes).WriteV3();
        var library = await ModLibraryIndex.BuildAsync(_fx.PenumbraFolder);
        var analysis = await Analyze(TestFixture.Character([new(Player, original, [Diffuse])], b => b.Redirects[original] = alt), library);

        var a = Assert.Single(analysis.Assets);
        Assert.Equal(ContentRepresentation.OriginalFromInstalledMod, a.Representation);
        Assert.True(a.IsOriginalContent);
        Assert.Equal(original, a.ContentHash);
        Assert.Equal(AssetProvenance.PenumbraModDirectory, a.Provenance);
        Assert.True(analysis.AllRequiredAvailable);

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        Assert.Equal(CompletenessStatus.Complete, result.Manifest.Completeness.Status);
        var f = Assert.Single(result.Manifest.Files);
        Assert.Equal(("OriginalFromInstalledMod", "hash"), (f.Representation, f.VerifiedAgainst));
        Assert.Equal(1, result.Manifest.Summary.OriginalsFromInstalledMods);
        Assert.Equal("Cool Top", f.Attribution.Single().ModName);
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        Assert.Equal(originalBytes, ReadAll(zip, f.ArchivePath!));
    }

    [Fact]
    public async Task InstalledModWithDifferentFileAtThatPath_DoesNotReplaceTheAlternate()
    {
        var original = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 6));
        var alt = _fx.AddCacheFile(TestFixture.Tex(256, 256, seed: 7), "tex");
        new ModBuilder(_fx.PenumbraFolder, "Other").Meta("Other").DefaultFile(Diffuse, "d.tex", TestFixture.Tex(512, 512, seed: 8)).WriteV3();
        var analysis = await Analyze(TestFixture.Character([new(Player, original, [Diffuse])], b => b.Redirects[original] = alt),
            await ModLibraryIndex.BuildAsync(_fx.PenumbraFolder));

        var a = Assert.Single(analysis.Assets);
        Assert.Equal(ContentRepresentation.CompressedAlternate, a.Representation);
        Assert.Equal(alt, a.ContentHash);
    }

    [Fact]
    public async Task OriginalWithUnrecognizedHeader_IsStillExportedUnchanged()
    {
        var bytes = TestFixture.RandomBytes(3000, 9);
        var hash = _fx.AddCacheFile(bytes, "tex");
        var analysis = await Analyze(TestFixture.Character([new(Player, hash, [Diffuse])]));

        var a = Assert.Single(analysis.Assets);
        Assert.Equal((AssetStatus.Available, ContentRepresentation.Original), (a.Status, a.Representation));
        Assert.False(a.Texture!.Valid);
        Assert.Contains("exported unchanged", a.Note);
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        Assert.Equal(CompletenessStatus.Complete, result.Manifest.Completeness.Status);
        Assert.False(result.Manifest.Files.Single().Texture!.HeaderValid);
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        Assert.Equal(bytes, ReadAll(zip, result.Manifest.Files.Single().ArchivePath!));
    }

    [Fact]
    public async Task CacheFilesAreAlreadyDecoded_AndAreNeverDecompressedAgain()
    {
        // PlayerSync's FileDownloadManager unwraps LZ4 before writing <hash>.<ext> and checks the SHA-1 of the result.
        // A file whose bytes happen to be LZ4-wrapped is therefore exported exactly as stored when its hash matches...
        var payload = TestFixture.Tex(64, 64, seed: 10);
        var wrapped = LZ4Wrapper.WrapHC(payload, 0, payload.Length);
        var storedHash = _fx.AddCacheFile(wrapped, "mdl");
        // ...and a still-wrapped copy of another file is reported as corrupt, not silently decoded.
        var expected = TestFixture.Sha1(payload);
        File.WriteAllBytes(Path.Combine(_fx.CacheFolder, expected.ToLowerInvariant() + ".tex"), wrapped);

        var analysis = await Analyze(TestFixture.Character(
            [new(Player, storedHash, ["chara/x/model/a.mdl"]), new(Player, expected, [Diffuse])]));
        Assert.Equal(AssetStatus.Available, analysis.Assets.Single(a => a.Hash == storedHash).Status);
        Assert.Equal(AssetStatus.HashMismatch, analysis.Assets.Single(a => a.Hash == expected).Status);

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: r => r.Mcdf = false));
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        Assert.Equal(wrapped, ReadAll(zip, result.Manifest.Files.Single(f => f.Hash == storedHash).ArchivePath!));
    }

    [Fact]
    public async Task MixedTextureOutcomes_AreEachReported_AndThePackageIsPartialWithReasons()
    {
        var ok = _fx.AddCacheFile(TestFixture.Tex(128, 128, seed: 11), "tex");
        var compressedOriginal = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 12));
        var goodAlt = _fx.AddCacheFile(TestFixture.Tex(256, 256, seed: 13), "tex");
        var brokenOriginal = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 14));
        var brokenAlt = _fx.AddCacheFile(TestFixture.RandomBytes(2000, 15), "tex");
        var missing = TestFixture.Sha1(TestFixture.Tex(512, 512, seed: 16));

        var analysis = await Analyze(TestFixture.Character(
            [new(Player, ok, [Diffuse]), new(Player, compressedOriginal, [Normal]), new(Player, brokenOriginal, [Mask]), new(Player, missing, [Spec])],
            b =>
            {
                b.Redirects[compressedOriginal] = goodAlt;
                b.Redirects[brokenOriginal] = brokenAlt;
            }));

        var byHash = analysis.Assets.ToDictionary(a => a.Hash);
        Assert.Equal((AssetStatus.Available, ContentRepresentation.Original), (byHash[ok].Status, byHash[ok].Representation));
        Assert.Equal((AssetStatus.Available, ContentRepresentation.CompressedAlternate), (byHash[compressedOriginal].Status, byHash[compressedOriginal].Representation));
        Assert.Equal(AssetStatus.InvalidContent, byHash[brokenOriginal].Status);
        Assert.Equal(AssetStatus.Missing, byHash[missing].Status);

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        var m = result.Manifest;
        Assert.Equal(CompletenessStatus.Partial, m.Completeness.Status);
        Assert.Equal((1, 1, 1, 2), (m.Summary.Missing, m.Summary.InvalidContent, m.Summary.CompressedAlternatesIncluded, m.Summary.IncludedFiles));
        Assert.Contains(m.Completeness.Reasons, r => r.StartsWith("1 file(s) are missing", StringComparison.Ordinal));
        Assert.Contains(m.Completeness.Reasons, r => r.StartsWith("1 compressed substitute(s) are not valid textures", StringComparison.Ordinal));
        Assert.Contains(m.Completeness.Reasons, r => r.StartsWith("1 texture(s) are PlayerSync's re-encoded", StringComparison.Ordinal));
        Assert.Null(m.Files.Single(f => f.Hash == missing).Representation);
        Assert.False(m.Files.Single(f => f.Hash == brokenOriginal).Included);
    }

    private static byte[] ReadAll(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
