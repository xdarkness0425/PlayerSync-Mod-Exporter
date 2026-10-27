using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Metadata;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using static PlayerSyncModExporter.Core.Model.SyncObjectKind;

namespace PlayerSyncModExporter.Tests;

/// <summary>Builds installed Penumbra mods in the fixture's mod directory, in FileVersion 3 or 4 layout.</summary>
internal sealed class ModBuilder(string modRoot, string folder)
{
    private readonly string _dir = Path.Combine(modRoot, folder);
    private readonly JsonObject _meta = new() { ["FileVersion"] = 3 };
    private readonly JsonObject _default = new() { ["Files"] = new JsonObject() };
    private readonly List<JsonObject> _groups = [];

    public ModBuilder Meta(string? name = null, string? author = null, string? version = null, string? description = null, string? website = null)
    {
        if (name != null) _meta["Name"] = name;
        if (author != null) _meta["Author"] = author;
        if (version != null) _meta["Version"] = version;
        if (description != null) _meta["Description"] = description;
        if (website != null) _meta["Website"] = website;
        return this;
    }

    /// <summary>Writes the file into the mod folder and maps the game path to it in the default container.</summary>
    public ModBuilder DefaultFile(string gamePath, string relativePath, byte[] content)
    {
        WriteFile(relativePath, content);
        ((JsonObject)_default["Files"]!)[gamePath] = relativePath.Replace('/', '\\');
        return this;
    }

    public ModBuilder Group(string name, string type, ulong defaultSettings, params (string Name, (string GamePath, string Rel, byte[] Content)[] Files)[] options)
    {
        var array = new JsonArray();
        var priority = 0;
        foreach (var (optionName, files) in options)
        {
            var map = new JsonObject();
            foreach (var (gamePath, rel, content) in files)
            {
                WriteFile(rel, content);
                map[gamePath] = rel.Replace('/', '\\');
            }

            array.Add(new JsonObject
            {
                ["Name"] = optionName, ["Description"] = optionName + " description", ["Priority"] = priority++, ["Files"] = map,
                ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray(),
            });
        }

        _groups.Add(new JsonObject
        {
            ["Name"] = name, ["Description"] = name + " group", ["Priority"] = 0, ["Type"] = type, ["DefaultSettings"] = defaultSettings,
            ["Options"] = array,
        });
        return this;
    }

    /// <summary>FileVersion 3: meta.json + default_mod.json + group_NNN_name.json.</summary>
    public void WriteV3()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "meta.json"), _meta.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "default_mod.json"), _default.ToJsonString());
        for (var i = 0; i < _groups.Count; i++)
            File.WriteAllText(Path.Combine(_dir, $"group_{i + 1:D3}_{_groups[i]["Name"]!.GetValue<string>().ToLowerInvariant()}.json"), _groups[i].ToJsonString());
    }

    /// <summary>FileVersion 4: everything in meta.json (Penumbra Files/ModSerialization.cs).</summary>
    public void WriteV4()
    {
        Directory.CreateDirectory(_dir);
        var meta = (JsonObject)_meta.DeepClone();
        meta["FileVersion"] = 4;
        meta["DefaultData"] = _default.DeepClone();
        meta["Groups"] = new JsonArray(_groups.Select(g => (JsonNode)g.DeepClone()).ToArray());
        File.WriteAllText(Path.Combine(_dir, "meta.json"), meta.ToJsonString());
    }

    private void WriteFile(string relativePath, byte[] content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}

public class MetadataRecoveryTests : IDisposable
{
    private const string Mdl = "chara/equipment/e6001/model/c0201e6001_top.mdl";
    private const string TexA = "chara/equipment/e6001/texture/v01_c0201e6001_top_d.tex";
    private const string TexB = "chara/equipment/e6001/texture/v01_c0201e6001_top_n.tex";

    private readonly TestFixture _fx = new();

    private readonly byte[] _model = TestFixture.RandomBytes(900, 10);
    private readonly byte[] _red = TestFixture.RandomBytes(700, 11);
    private readonly byte[] _blue = TestFixture.RandomBytes(700, 12);
    private readonly byte[] _normal = TestFixture.RandomBytes(500, 13);

    public void Dispose() => _fx.Dispose();

    private ModBuilder Mod(string folder) => new(_fx.PenumbraFolder, folder);

    /// <summary>A FileVersion 3 mod: model in the default container, a Single "Color" group (Red, Blue; author default Blue).</summary>
    private void InstallCoolTop(Action<ModBuilder>? configure = null)
    {
        var mod = Mod("Cool Top").Meta("Cool Top", "Author A", "1.2", "A cool top.", "https://example.invalid/cool-top")
            .DefaultFile(Mdl, "model/top.mdl", _model)
            .Group("Color", "Single", 1,
                ("Red", [(TexA, "red/d.tex", _red)]),
                ("Blue", [(TexA, "blue/d.tex", _blue)]));
        configure?.Invoke(mod);
        mod.WriteV3();
    }

    private async Task<AnalysisResult> AnalyzeAsync(IEnumerable<CapturedFileReplacement> files)
    {
        var library = await ModLibraryIndex.BuildAsync(_fx.PenumbraFolder);
        return await _fx.Analyzer().AnalyzeAsync(TestFixture.Character(files), new AssetAnalyzerOptions { ModLibrary = library });
    }

    private static JsonNode Entry(ZipArchive zip, string name) => JsonNode.Parse(zip.GetEntry(name)!.Open())!;

    [Fact]
    public async Task KnownGoodV3Mod_RecoversNamesAndReproducesTheUsedSingleOption()
    {
        InstallCoolTop();
        var hm = _fx.AddCacheFile(_model, "mdl");
        var hr = _fx.AddCacheFile(_red, "tex");
        var analysis = await AnalyzeAsync([new(Player, hm, [Mdl]), new(Player, hr, [TexA])]);

        Assert.Equal(MetadataClassification.Recovered, analysis.Metadata.Find(hm, Mdl)!.Classification);
        Assert.Null(analysis.Metadata.Find(hm, Mdl)!.Group); // default container
        var tex = analysis.Metadata.Find(hr, TexA)!;
        Assert.Equal("Color", tex.Group!.Name);
        Assert.Equal("Red", Assert.Single(tex.Options).Name);

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: r => r.Mcdf = false));
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        var groupFile = Assert.Single(zip.Entries, e => e.FullName.StartsWith("group_", StringComparison.Ordinal));
        Assert.Equal("group_001_cool top - color.json", groupFile.FullName);
        var group = Entry(zip, groupFile.FullName);
        Assert.Equal("Cool Top - Color", group["Name"]!.GetValue<string>());
        Assert.Equal("Single", group["Type"]!.GetValue<string>());
        Assert.Equal(0UL, group["DefaultSettings"]!.GetValue<ulong>());
        var option = Assert.Single(group["Options"]!.AsArray())!;
        Assert.Equal("Red", option["Name"]!.GetValue<string>());
        Assert.Equal("Red description", option["Description"]!.GetValue<string>());
        var target = option["Files"]![TexA]!.GetValue<string>().Replace('\\', '/');
        Assert.Equal(TestFixture.Sha1(_red), TestFixture.Sha1(ReadAll(zip, target)));

        // The model stays in the default option; the texture is only in the group.
        var defaults = Entry(zip, PmpWriter.DefaultModEntry)["Files"]!.AsObject();
        Assert.True(defaults.ContainsKey(Mdl));
        Assert.False(defaults.ContainsKey(TexA));

        var source = Assert.Single(result.Manifest.Metadata.SourceMods);
        Assert.Equal(("Cool Top", "Recovered"), (source.Name.Value, source.Name.Classification));
        Assert.Equal(("Author A", "Recovered"), (source.Author.Value, source.Author.Classification));
        Assert.Equal("1.2", source.Version.Value);
        var g = Assert.Single(source.Groups);
        Assert.Equal(("Blue", "Recovered"), (g.AuthorDefault.Value, g.AuthorDefault.Classification));
        Assert.Equal(("Red", "Inferred"), (g.InferredSelection.Value, g.InferredSelection.Classification));
        Assert.Equal(["Red"], g.ReproducedOptions);
        Assert.Equal(2, g.TotalOptions);
        Assert.Equal(2, result.Manifest.Metadata.AttributedPaths);
        Assert.Equal(0, result.Manifest.Metadata.UnattributedPaths);
        var attribution = Assert.Single(result.Manifest.Files.Single(f => f.Hash == hr).Attribution);
        Assert.Equal(("group", "Recovered", "Cool Top", "Color"), (attribution.Placement, attribution.Classification, attribution.ModName, attribution.Group));
        Assert.Null(attribution.Note);
        Assert.Contains("Cool Top by Author A v1.2", Entry(zip, PmpWriter.MetaEntry)["Description"]!.GetValue<string>());
    }

    [Fact]
    public async Task V4Mod_MultiGroup_KeepsOriginalOrderAndEnablesUsedOptions()
    {
        var a = TestFixture.RandomBytes(100, 20);
        var b = TestFixture.RandomBytes(100, 21);
        var c = TestFixture.RandomBytes(100, 22);
        const string pa = "chara/equipment/e0100/model/c0101e0100_met.mdl";
        const string pb = "chara/equipment/e0100/model/c0101e0100_glv.mdl";
        const string pc = "chara/equipment/e0100/model/c0101e0100_sho.mdl";
        Mod("Extras Pack").Meta("Extras Pack", "B")
            .Group("Extras", "Multi", 2, ("Hat", [(pa, "a.mdl", a)]), ("Gloves", [(pb, "b.mdl", b)]), ("Shoes", [(pc, "c.mdl", c)]))
            .WriteV4();
        var hc = _fx.AddCacheFile(c, "mdl");
        var ha = _fx.AddCacheFile(a, "mdl");
        var analysis = await AnalyzeAsync([new(Player, hc, [pc]), new(Player, ha, [pa])]);

        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: r => r.Mcdf = false));
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        var group = Entry(zip, "group_001_extras pack - extras.json");
        Assert.Equal("Multi", group["Type"]!.GetValue<string>());
        Assert.Equal(3UL, group["DefaultSettings"]!.GetValue<ulong>());
        var options = group["Options"]!.AsArray();
        Assert.Equal(["Hat", "Shoes"], options.Select(o => o!["Name"]!.GetValue<string>()));
        Assert.Equal([0, 2], options.Select(o => o!["Priority"]!.GetValue<int>()));
        var g = Assert.Single(Assert.Single(result.Manifest.Metadata.SourceMods).Groups);
        Assert.Equal("Gloves", g.AuthorDefault.Value);
        Assert.Equal("Hat, Shoes", g.InferredSelection.Value);
        Assert.Equal(4, Assert.Single(result.Manifest.Metadata.SourceMods).FileVersion);
    }

    [Fact]
    public async Task NoInstalledCopy_FallsBackToDefaultOption_WithInferredCategoryAndUnavailableFields()
    {
        var hm = _fx.AddCacheFile(_model, "mdl");
        var analysis = await AnalyzeAsync([new(Player, hm, [Mdl])]);
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis, configure: r => r.Mcdf = false));

        using var zip = ZipFile.OpenRead(result.PmpPath!);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("group_", StringComparison.Ordinal));
        Assert.True(Entry(zip, PmpWriter.DefaultModEntry)["Files"]!.AsObject().ContainsKey(Mdl));
        var attribution = Assert.Single(result.Manifest.Files.Single().Attribution);
        Assert.Equal(("unattributed", "Unavailable"), (attribution.Placement, attribution.Classification));
        Assert.Null(attribution.ModName);
        Assert.Equal(("Equipment e6001 (body)", "Inferred"), (attribution.InferredCategory.Value, attribution.InferredCategory.Classification));
        Assert.Empty(result.Manifest.Metadata.SourceMods);
        Assert.Equal(1, result.Manifest.Metadata.UnattributedPaths);
        Assert.Contains(result.Manifest.Metadata.Unavailable, u => u.Contains("mod name, author", StringComparison.Ordinal));
        Assert.Equal("Unknown (reconstructed from PlayerSync cache)", Entry(zip, PmpWriter.MetaEntry)["Author"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithoutModLibrary_NothingIsAttributed()
    {
        InstallCoolTop();
        var hm = _fx.AddCacheFile(_model, "mdl");
        var analysis = await _fx.Analyzer().AnalyzeAsync(TestFixture.Character([new(Player, hm, [Mdl])]));
        Assert.False(analysis.Metadata.LibraryAvailable);
        var result = await new ExportPackager().ExportAsync(_fx.Request(analysis));
        Assert.Contains(result.Manifest.Metadata.Unavailable, u => u.Contains("could not be read", StringComparison.Ordinal));
        Assert.Empty(new ExportPackager().Plan(_fx.Request(analysis, "x")).Groups);
    }

    [Fact]
    public async Task SameGamePathWithDifferentContent_IsNotAttributed()
    {
        InstallCoolTop();
        var other = _fx.AddCacheFile(TestFixture.RandomBytes(700, 99), "tex");
        var analysis = await AnalyzeAsync([new(Player, other, [TexA])]);
        Assert.Null(analysis.Metadata.Find(other, TexA));
        Assert.Empty(analysis.Metadata.SourceMods);
    }

    [Fact]
    public async Task SameContentAtDifferentGamePath_IsNotAttributed()
    {
        InstallCoolTop();
        var hr = _fx.AddCacheFile(_red, "tex");
        var analysis = await AnalyzeAsync([new(Player, hr, [TexB])]);
        Assert.Null(analysis.Metadata.Find(hr, TexB));
    }

    [Fact]
    public async Task IdenticalFilesInTwoMods_TieIsUnavailable_ClearWinnerIsInferred()
    {
        Mod("Copy One").Meta("Copy One").DefaultFile(Mdl, "m.mdl", _model).WriteV3();
        Mod("Copy Two").Meta("Copy Two").DefaultFile(Mdl, "m.mdl", _model).WriteV3();
        var hm = _fx.AddCacheFile(_model, "mdl");

        var tie = await AnalyzeAsync([new(Player, hm, [Mdl])]);
        var a = tie.Metadata.Find(hm, Mdl)!;
        Assert.Equal(MetadataClassification.Unavailable, a.Classification);
        Assert.Null(a.Mod);
        Assert.Equal(["Copy One", "Copy Two"], a.CandidateMods);

        // Copy Two also contains the normal map the character uses, so it explains more of the character.
        Mod("Copy Two").Meta("Copy Two").DefaultFile(Mdl, "m.mdl", _model).DefaultFile(TexB, "n.tex", _normal).WriteV3();
        var hn = _fx.AddCacheFile(_normal, "tex");
        var winner = await AnalyzeAsync([new(Player, hm, [Mdl]), new(Player, hn, [TexB])]);
        var w = winner.Metadata.Find(hm, Mdl)!;
        Assert.Equal(MetadataClassification.Inferred, w.Classification);
        Assert.Equal("Copy Two", w.Mod!.Name);
        Assert.Equal(MetadataClassification.Recovered, winner.Metadata.Find(hn, TexB)!.Classification);

        var result = await new ExportPackager().ExportAsync(_fx.Request(winner));
        Assert.Equal(["Copy One", "Copy Two"], result.Manifest.Files.Single(f => f.Hash == hm).Attribution.Single().CandidateMods);
    }

    [Fact]
    public async Task FileOnlyInAnUnusedSingleOption_StaysInDefaultOptionWithNote()
    {
        // Red holds two textures; Blue holds a different normal map. The character uses Red's diffuse and normal plus Blue's normal
        // for another path, which a single-choice group cannot express.
        const string texC = "chara/equipment/e6001/texture/v01_c0201e6001_top_m.tex";
        var mask = TestFixture.RandomBytes(300, 30);
        Mod("Cool Top").Meta("Cool Top")
            .Group("Color", "Single", 0,
                ("Red", [(TexA, "red/d.tex", _red), (TexB, "red/n.tex", _normal)]),
                ("Blue", [(texC, "blue/m.tex", mask)]))
            .WriteV3();
        var hr = _fx.AddCacheFile(_red, "tex");
        var hn = _fx.AddCacheFile(_normal, "tex");
        var hm = _fx.AddCacheFile(mask, "tex");
        var analysis = await AnalyzeAsync([new(Player, hr, [TexA]), new(Player, hn, [TexB]), new(Player, hm, [texC])]);

        var stray = analysis.Metadata.Find(hm, texC)!;
        Assert.Null(stray.Group);
        Assert.Contains("default option", stray.Note);
        var plan = new ExportPackager().Plan(_fx.Request(analysis));
        Assert.True(plan.DefaultRedirections.ContainsKey(texC));
        Assert.Equal("Red", Assert.Single(Assert.Single(plan.Groups).Options).Source.Name);
        Assert.Equal(3, plan.Redirections.Count);
    }

    [Fact]
    public async Task PartiallyPopulatedMetadata_MarksMissingFieldsUnavailable()
    {
        Mod("folder-name-only").DefaultFile(Mdl, "m.mdl", _model).WriteV3(); // meta.json without Name, Author, Version
        var hm = _fx.AddCacheFile(_model, "mdl");
        var result = await new ExportPackager().ExportAsync(_fx.Request(await AnalyzeAsync([new(Player, hm, [Mdl])])));

        var source = Assert.Single(result.Manifest.Metadata.SourceMods);
        Assert.Equal(("Unavailable", null), (source.Name.Classification, source.Name.Value));
        Assert.Equal("Unavailable", source.Author.Classification);
        Assert.Equal("Unavailable", source.Version.Classification);
        Assert.Equal("folder-name-only", source.Folder);
        Assert.Equal("folder-name-only", result.Manifest.Files.Single().Attribution.Single().ModName); // display only
    }

    [Fact]
    public async Task MalformedAndHostileMods_AreSkippedWithProblems()
    {
        Directory.CreateDirectory(Path.Combine(_fx.PenumbraFolder, "Broken"));
        File.WriteAllText(Path.Combine(_fx.PenumbraFolder, "Broken", "meta.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(_fx.PenumbraFolder, "NotAnObject"));
        File.WriteAllText(Path.Combine(_fx.PenumbraFolder, "NotAnObject", "meta.json"), "[1,2]");
        Directory.CreateDirectory(Path.Combine(_fx.PenumbraFolder, "Escape"));
        File.WriteAllText(Path.Combine(_fx.PenumbraFolder, "Escape", "meta.json"), """{"Name":"Escape"}""");
        File.WriteAllText(Path.Combine(_fx.PenumbraFolder, "Escape", "default_mod.json"),
            $$$"""{"Files":{"{{{Mdl}}}":"..\\..\\PlayerSyncCache\\x.mdl","C:/abs.tex":"a.tex","{{{TexA}}}":"C:\\abs\\a.tex"}}""");
        Directory.CreateDirectory(Path.Combine(_fx.PenumbraFolder, "NoMeta"));

        var library = await ModLibraryIndex.BuildAsync(_fx.PenumbraFolder);
        Assert.Equal(2, library.Problems.Count);
        var escape = Assert.Single(library.Mods);
        Assert.Empty(escape.Default.Files);
        Assert.Empty(library.Lookup(Mdl));
    }

    [Fact]
    public async Task Limits_StopOversizedMods()
    {
        InstallCoolTop();
        var library = await ModLibraryIndex.BuildAsync(_fx.PenumbraFolder, new ModLibraryLimits { MaxRedirectionsPerMod = 1 });
        Assert.Empty(library.Mods);
        Assert.Contains("too many file redirections", Assert.Single(library.Problems));

        var tiny = await ModLibraryIndex.BuildAsync(_fx.PenumbraFolder, new ModLibraryLimits { MaxJsonBytes = 10 });
        Assert.Contains("larger than", Assert.Single(tiny.Problems));
    }

    [Fact]
    public async Task IdenticalInputs_ProduceIdenticalPackageMetadata()
    {
        InstallCoolTop();
        var hm = _fx.AddCacheFile(_model, "mdl");
        var hr = _fx.AddCacheFile(_red, "tex");
        var files = new CapturedFileReplacement[] { new(Player, hr, [TexA]), new(Player, hm, [Mdl]) };

        async Task<Dictionary<string, string>> ExportOnce(string name)
        {
            var result = await new ExportPackager().ExportAsync(_fx.Request(await AnalyzeAsync(files), name, r => r.Mcdf = false));
            using var zip = ZipFile.OpenRead(result.PmpPath!);
            return zip.Entries.Where(e => e.FullName != PmpWriter.ManifestEntry)
                .ToDictionary(e => e.FullName, e => TestFixture.Sha1(ReadAll(zip, e.FullName)));
        }

        var first = await ExportOnce("Same");
        var second = await ExportOnce("Same (copy)");
        // Only the package name differs, which appears in meta.json.
        first.Remove(PmpWriter.MetaEntry);
        second.Remove(PmpWriter.MetaEntry);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ExportedPmpWithGroups_SatisfiesPenumbraGroupRules()
    {
        InstallCoolTop();
        var hm = _fx.AddCacheFile(_model, "mdl");
        var hr = _fx.AddCacheFile(_red, "tex");
        var result = await new ExportPackager().ExportAsync(_fx.Request(await AnalyzeAsync([new(Player, hm, [Mdl]), new(Player, hr, [TexA])])));

        Assert.Contains(result.VerificationNotes, n => n.Contains("1 option groups (1 redirections)", StringComparison.Ordinal));
        using var zip = ZipFile.OpenRead(result.PmpPath!);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("group_", StringComparison.Ordinal)))
        {
            // Penumbra FilenameService: group_{index + 1:D3}_{name}.json; GroupDeserialization needs Type and Options.
            Assert.Matches(@"^group_\d{3}_[a-z0-9 _-]+\.json$", entry.FullName);
            var g = Entry(zip, entry.FullName);
            Assert.Contains(g["Type"]!.GetValue<string>(), new[] { "Single", "Multi" });
            foreach (var option in g["Options"]!.AsArray())
            foreach (var (_, rel) in option!["Files"]!.AsObject())
                Assert.NotNull(zip.GetEntry(rel!.GetValue<string>().Replace('\\', '/')));
        }

        // The manifest round-trips with the new sections.
        var manifest = JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(result.ManifestPath), ExportManifest.JsonOptions)!;
        Assert.Equal("Cool Top", manifest.Metadata.SourceMods.Single().Name.Value);
        Assert.Equal(CompletenessStatus.Complete, manifest.Completeness.Status);
    }

    [Theory]
    [InlineData("chara/equipment/e0123/model/c0101e0123_dwn.mdl", "Equipment e0123 (legs)")]
    [InlineData("chara/accessory/a0005/texture/v01_c0101a0005_ear_d.tex", "Accessory a0005 (earrings)")]
    [InlineData("chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl", "Weapon w0201 body b0001")]
    [InlineData("chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl", "Character c0101 hair h0005")]
    [InlineData("chara/monster/m0001/obj/body/b0001/model/m0001b0001.mdl", "Monster m0001 body b0001")]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/idle.pap", "Animation")]
    [InlineData("vfx/common/eff/x.avfx", "Visual effect")]
    [InlineData("sound/x/y.scd", "Sound")]
    [InlineData("ui/icon/000000/000001.tex", "Other game file")]
    public void GamePathCategory_DescribesTheReplacedItem(string gamePath, string expected)
        => Assert.Equal(expected, GamePathCategory.Describe(gamePath));

    private static byte[] ReadAll(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
