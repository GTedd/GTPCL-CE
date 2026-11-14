using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Horizon;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class HorizonModsTest
{
    private const string Horizon = "9f42064aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Mixins = "0123456789abcdef0123456789abcdef01234567";

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcl-horizon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // ===== TOML =====

    [TestMethod]
    public void TomlParsesManifestSubset()
    {
        var table = HorizonToml.Parse(
            """
            # comment
            id = "my_mod"                # trailing comment
            name = 'Literal \name'
            authors = [
              "A",
              "B", # inner comment
            ]
            api = "0.2"
            hooks = 1_000
            ratio = 0.5
            on = true
            point = { x = 1, y = "two" }

            [mod]
            id = "zoom"
            "quoted key".nested = 3

            [[bin]]
            name = "a"
            [[bin]]
            name = "b"
            """);
        Assert.AreEqual("my_mod", HorizonToml.GetString(table, "id"));
        Assert.AreEqual(@"Literal \name", HorizonToml.GetString(table, "name"));
        CollectionAssert.AreEqual(new[] { "A", "B" }, HorizonToml.GetStrings(table, "authors"));
        Assert.AreEqual(1000L, HorizonToml.GetLong(table, "hooks"));
        Assert.AreEqual(0.5, HorizonToml.Get(table, "ratio"));
        Assert.AreEqual(true, HorizonToml.Get(table, "on"));
        Assert.AreEqual("two", HorizonToml.GetString(table, "point.y"));
        Assert.AreEqual("zoom", HorizonToml.GetString(table, "mod.id"));
        Assert.AreEqual(3L, HorizonToml.GetLong(table, "mod.quoted key.nested"));
        Assert.AreEqual(2, ((List<object?>)table["bin"]!).Count);
    }

    [TestMethod]
    public void TomlParsesStringsAndRejectsDuplicates()
    {
        var table = HorizonToml.Parse("a = \"tab\\tq\\\"\\u00e9\"\nb = \"\"\"\nline1\nline2\"\"\"\nc = '''raw\\n'''\n");
        Assert.AreEqual("tab\tq\"é", HorizonToml.GetString(table, "a"));
        Assert.AreEqual("line1\nline2", HorizonToml.GetString(table, "b"));
        Assert.AreEqual("raw\\n", HorizonToml.GetString(table, "c"));
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonToml.Parse("a = 1\na = 2\n"));
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonToml.Parse("a = \"unterminated\n"));
    }

    // ===== 模组文件 =====

    [TestMethod]
    public void ReadsContentMod()
    {
        var path = _Zip("zoomies.hmod", new Dictionary<string, string>
        {
            ["horizon-mod.toml"] = _ContentManifest("zoomies"),
            ["data/zoomies/recipe/a.json"] = "{}"
        });
        var info = HorizonModFile.Read(path);
        Assert.AreEqual(HorizonModKind.Content, info.Kind);
        Assert.AreEqual("zoomies", info.Id);
        Assert.AreEqual("Zoomies", info.Name);
        Assert.AreEqual("0.2", info.Api);
        CollectionAssert.AreEqual(new[] { "registries" }, info.Capabilities.ToArray());
    }

    [TestMethod]
    public void ReadsNativeMod()
    {
        var path = _Zip("fps.hnmod", _NativeFiles("fps"));
        var info = HorizonModFile.Read(path);
        Assert.AreEqual(HorizonModKind.Native, info.Kind);
        Assert.AreEqual("fps", info.Id);
        Assert.AreEqual("1.0.0", info.Version);
        Assert.AreEqual(Horizon, info.Native!.Horizon);
        Assert.AreEqual(5, info.Native.Hooks);
    }

    [TestMethod]
    public void RejectsBadNativeBundles()
    {
        var unsafePath = _NativeFiles("fps");
        unsafePath["mixins/fps/../evil.rs"] = "";
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonModFile.Read(_Zip("a.hnmod", unsafePath)));

        var binary = _NativeFiles("fps");
        binary["mixins/fps/payload.exe"] = "MZ";
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonModFile.Read(_Zip("b.hnmod", binary)));

        var unknownField = _NativeFiles("fps");
        unknownField["horizon-native.json"] = unknownField["horizon-native.json"].Replace("\"schema\"", "\"extra\": 1, \"schema\"");
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonModFile.Read(_Zip("c.hnmod", unknownField)));

        var withContent = _NativeFiles("fps");
        withContent["horizon-mod.toml"] = _ContentManifest("fps");
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonModFile.Read(_Zip("d.hnmod", withContent)),
            ".hnmod 不能带内容清单");

        var mismatch = _NativeFiles("fps");
        mismatch["mixins/fps/horizon-mixin.toml"] = mismatch["mixins/fps/horizon-mixin.toml"].Replace("1.0.0", "2.0.0");
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonModFile.Read(_Zip("e.hnmod", mismatch)));
    }

    [TestMethod]
    public void ReadsCombinedHmod()
    {
        var files = _NativeFiles("veins");
        files["horizon-mod.toml"] = _ContentManifest("veins");
        files["assets/veins/lang/en_us.json"] = "{}";
        var info = HorizonModFile.Read(_Zip("veins.hmod", files));
        Assert.AreEqual(HorizonModKind.ContentAndNative, info.Kind);
        Assert.IsTrue(info.HasNativeCode);
    }

    // ===== 游戏目录 =====

    [TestMethod]
    public void EnabledListKeepsOtherLines()
    {
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "horizon-mods.txt"), "mod.a:true\nsomeSetting:5\nmod.b:false\n");

        HorizonGameContent.SetEnabled(game, "c", true);
        HorizonGameContent.SetEnabled(game, "a", false);

        Assert.AreEqual("someSetting:5\nmod.b:false\nmod.c:true\n", File.ReadAllText(Path.Combine(game, "horizon-mods.txt")));
        CollectionAssert.AreEquivalent(new[] { "c" }, HorizonGameContent.ReadEnabled(game).ToArray());
    }

    [TestMethod]
    public void InstallsIntoTheRightFolders()
    {
        var game = Path.Combine(_dir, "game");
        var content = _Zip("Some Name.hmod", new Dictionary<string, string> { ["horizon-mod.toml"] = _ContentManifest("zoomies") });
        var native = _Zip("download (1).hnmod", _NativeFiles("fps"));

        HorizonGameContent.Install(game, content, false);
        HorizonGameContent.Install(game, native, false);

        Assert.IsTrue(File.Exists(Path.Combine(game, "horizon-mods", "zoomies.hmod")));
        Assert.IsTrue(File.Exists(Path.Combine(game, "horizon-native-mods", "fps.hnmod")));
        var list = HorizonGameContent.List(game);
        Assert.AreEqual(2, list.Count);
        Assert.IsTrue(list.All(m => m.Enabled && m.Error is null));
        Assert.ThrowsExactly<IOException>(() => HorizonGameContent.Install(game, content, false));

        HorizonGameContent.Remove(game, list.Single(m => m.Id == "fps"));
        Assert.IsFalse(File.Exists(Path.Combine(game, "horizon-native-mods", "fps.hnmod")));
        CollectionAssert.AreEquivalent(new[] { "zoomies" }, HorizonGameContent.ReadEnabled(game).ToArray());
    }

    [TestMethod]
    public void ListFlagsMisplacedNativeMod()
    {
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(Path.Combine(game, "horizon-mods"));
        File.Copy(_Zip("fps.hnmod", _NativeFiles("fps")), Path.Combine(game, "horizon-mods", "fps.hmod"));
        var mod = HorizonGameContent.List(game).Single();
        Assert.IsNotNull(mod.Error);
    }

    [TestMethod]
    public void ParsesLaunchApiContent()
    {
        var support = HorizonContentSupport.Parse(JsonNode.Parse(
            """{"mod":{"api":["0.2","0.3"]},"native_mod":{"hooks":415,"rebuild":true},"resourcepack":{"format":[97,1]},"shader":{"api":["0.1"],"schema":1}}""")!.AsObject());
        Assert.IsTrue(support.SupportsModApi("0.3"));
        Assert.IsFalse(support.SupportsModApi("0.1"));
        Assert.IsTrue(support.SupportsNativeHooks(415));
        Assert.IsFalse(support.SupportsNativeHooks(416));
        Assert.AreEqual((97, 1), support.ResourcePackFormat);
        Assert.IsTrue(support.NativeRebuild);
    }

    private static string _ContentManifest(string id) =>
        $"""
         id = "{id}"
         name = "{char.ToUpperInvariant(id[0])}{id[1..]}"
         version = "1.0.0"
         description = "test"
         authors = ["me"]
         api = "0.2"
         side = "both"
         capabilities = ["registries"]
         """;

    private static Dictionary<string, string> _NativeFiles(string id) => new()
    {
        ["horizon-native.json"] =
            $$"""{"schema": 1, "id": "{{id}}", "version": "1.0.0", "game": "26.3", "horizon": "{{Horizon}}", "mixins": "{{Mixins}}", "hooks": 5}""",
        [$"mixins/{id}/horizon-mixin.toml"] =
            $"[mod]\nid = \"{id}\"\nversion = \"1.0.0\"\n\n[compatibility]\nhorizon_revision = \"{Horizon}\"\n",
        [$"mixins/{id}/src/lib.rs"] = "pub fn x() {}",
        [$"mixins/{id}/data/table.json"] = "{}",
        [$"mixins/{id}/README.md"] = "readme"
    };

    private string _Zip(string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, text) in files)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        return path;
    }
}
