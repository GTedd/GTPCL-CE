using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Horizon;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class HorizonNativeFitTest
{
    private const string Played = "9f42064c11efe7ef3e5ed2baad7ddc2a3b36eea0";
    private const string Older = "fdc87aecdbd116bf1e08adcd062fcbaea8c2e2ca";
    private const string Mixins = "d9c83d7096b7ea5b83678da2efdfd6161f74997e";
    private const string Sha = "5fef518e6ce7417944ab2621711be013cf7e25fdd6d3b25922876e78f090bfa3";

    private static HorizonNativeManifest Manifest(string pin, int? hooks = null) =>
        new("mod_menu", "0.3.5", "26.3", pin, Mixins, hooks);

    private static HorizonRegistryVersion Registry(string version, string pin, string sha, int? hooks = null,
        params HorizonRegistryBroken[] broken) =>
        new(version, "2026-11-09", "", $"native-1/{pin}/{Mixins}", null, [], null, hooks, "26.3", broken, [],
            new HorizonRegistryFile($"mod_menu-{version}.hnmod", "https://github.com/x", 1, sha, null));

    private static readonly HorizonNativeFailure[] NoFailures = [];

    [TestMethod]
    public void RevisionsCompareByPrefix()
    {
        Assert.IsTrue(HorizonNativeFitCheck.SameRevision(Played, "9f42064"));
        Assert.IsTrue(HorizonNativeFitCheck.SameRevision("9F42064", Played));
        Assert.IsFalse(HorizonNativeFitCheck.SameRevision(Played, Older));
        Assert.IsFalse(HorizonNativeFitCheck.SameRevision(Played, "9f4"));
        Assert.IsTrue(HorizonNativeFitCheck.SameRevision("v0.2.97", "v0.2.97"));
        Assert.IsFalse(HorizonNativeFitCheck.SameRevision(null, Played));
    }

    [TestMethod]
    public void RegistryBrokenOnThePlayedHorizonIsLeftOut()
    {
        var registry = Registry("0.3.5", Older, Sha, null, new HorizonRegistryBroken(Played, "E0609"));
        var fit = HorizonNativeFitCheck.Check(Manifest(Older), Sha, "9f42064", 415, registry, NoFailures);
        Assert.AreEqual(HorizonNativeFitKind.Broken, fit.Kind);
        Assert.AreEqual("E0609", fit.Code);
        Assert.IsTrue(fit.FromRegistry);
    }

    [TestMethod]
    public void BrokenOnAnotherHorizonDoesNotCount()
    {
        var registry = Registry("0.3.5", Played, Sha, null, new HorizonRegistryBroken(Older, "E0609"));
        Assert.AreEqual(HorizonNativeFitKind.Fits,
            HorizonNativeFitCheck.Check(Manifest(Played), Sha, Played, 415, registry, NoFailures).Kind);
    }

    [TestMethod]
    public void LocalFailureOfTheSameFileIsLeftOut()
    {
        HorizonNativeFailure[] failures = [new("mod_menu", "0.3.5", Sha, Played, "E0609", "error[E0609]", "2026-11-15")];
        var fit = HorizonNativeFitCheck.Check(Manifest(Older), Sha, "9f42064", 415, null, failures);
        Assert.AreEqual(HorizonNativeFitKind.Broken, fit.Kind);
        Assert.IsFalse(fit.FromRegistry);

        // 换了文件（SHA-256 不同）就重新尝试
        Assert.AreEqual(HorizonNativeFitKind.OtherVersion,
            HorizonNativeFitCheck.Check(Manifest(Older), "00" + Sha[2..], "9f42064", 415, null, failures).Kind);
    }

    [TestMethod]
    public void HookModsFitByHookVersion()
    {
        Assert.AreEqual(HorizonNativeFitKind.Fits, HorizonNativeFitCheck.Check(Manifest(Older, 1), Sha, Played, 415, null, NoFailures).Kind);
        var fit = HorizonNativeFitCheck.Check(Manifest(Older, 500), Sha, Played, 415, null, NoFailures);
        Assert.AreEqual(HorizonNativeFitKind.NeedsNewer, fit.Kind);
        Assert.AreEqual(500, fit.Needs);
        Assert.AreEqual(415, fit.Has);
    }

    [TestMethod]
    public void PinnedModsFitOnlyTheirCommit()
    {
        Assert.AreEqual(HorizonNativeFitKind.Fits, HorizonNativeFitCheck.Check(Manifest(Played), Sha, "9f42064", 415, null, NoFailures).Kind);
        var other = HorizonNativeFitCheck.Check(Manifest(Older), Sha, "9f42064", 415, null, NoFailures);
        Assert.AreEqual(HorizonNativeFitKind.OtherVersion, other.Kind);
        Assert.AreEqual(Older, other.Pin);
    }

    [TestMethod]
    public void FailuresAreRecordedAndReplaced()
    {
        var file = Path.Combine(Path.GetTempPath(), "pcl-test-" + Guid.NewGuid().ToString("N"), HorizonNativeFitCheck.FailuresFileName);
        try
        {
            Assert.AreEqual(0, HorizonNativeFitCheck.ReadFailures(file).Count);
            HorizonNativeFitCheck.RecordFailure(file, new HorizonNativeFailure("mod_menu", "0.3.5", Sha, Played, "E0609", "a", "d1"));
            HorizonNativeFitCheck.RecordFailure(file, new HorizonNativeFailure("mod_menu", "0.3.5", Sha, Played, "E0425", "b", "d2"));
            HorizonNativeFitCheck.RecordFailure(file, new HorizonNativeFailure("zoom", "2.0.0", null, Played, "E0599", "c", "d3"));
            var failures = HorizonNativeFitCheck.ReadFailures(file);
            Assert.AreEqual(2, failures.Count);
            Assert.AreEqual("E0425", HorizonNativeFitCheck.FindFailure(failures, "mod_menu", "0.3.5", Sha, "9f42064")?.Code);
            Assert.AreEqual("E0599", HorizonNativeFitCheck.FindFailure(failures, "zoom", "2.0.0", null, Played)?.Code);
            Assert.IsNull(HorizonNativeFitCheck.FindFailure(failures, "mod_menu", "0.3.5", Sha, Older));

            File.WriteAllText(file, "not json");
            Assert.AreEqual(0, HorizonNativeFitCheck.ReadFailures(file).Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [TestMethod]
    public void CompileErrorIsTracedToTheModThatWroteTheCode()
    {
        // 26.3-Horizon 上 mod_menu 0.3.5 的真实报错（build.log）
        const string error = """
                             error[E0609]: no field `options_to_game` on type `&mut game::Game<'_>`
                                  --> crates\horizon-client\src\game.rs:13850:72
                                   |
                             13850 |             let (from_title, to_game) = (game.options_from_title, game.options_to_game);
                                   |                                                                        ^^^^^^^^^^^^^^^ unknown field
                                   |
                                   = note: available fields are: `res`, `rest_loader`, `pending_entry`, `debug_density`, `pending_pack_reload` ... and 95 others
                             """;
        const string error2 = """
                              error[E0609]: no field `options_to_game` on type `&mut game::Game<'_>`
                                   --> crates\horizon-client\src\game.rs:13852:18
                                    |
                              13852 |             game.options_to_game = to_game;
                                    |                  ^^^^^^^^^^^^^^^ unknown field
                              """;
        var modMenu = new Dictionary<string, byte[]>
        {
            ["horizon-mixin.toml"] = Encoding.UTF8.GetBytes("[mod]\nid = \"mod_menu\"\n"),
            ["ui.rs"] = Encoding.UTF8.GetBytes(
                "    if let Some(index) = settings {\r\n        shared::close();\r\n" +
                "        let (from_title, to_game) = (game.options_from_title, game.options_to_game);\r\n" +
                "        game.open_options_at(from_title, Page::NativeMod(index));\r\n        game.options_to_game = to_game;\r\n    }\r\n")
        };
        var zoom = new Dictionary<string, byte[]>
        {
            ["zoom.rs"] = Encoding.UTF8.GetBytes("pub fn zoom(game: &mut Game<'_>) {\n    game.zoom = 2.0;\n}\n")
        };

        var culprit = HorizonNativeFitCheck.FindCompileCulprit([error, error2],
            [("zoom", zoom), ("mod_menu", modMenu)]);
        Assert.IsNotNull(culprit);
        Assert.AreEqual("mod_menu", culprit.Value.Id);
        Assert.AreEqual("E0609", culprit.Value.Code);

        // Horizon 自己的代码出错时找不到模组
        Assert.IsNull(HorizonNativeFitCheck.FindCompileCulprit(
            ["error[E0308]: mismatched types\n  --> crates\\horizon-client\\src\\game.rs:10:5\n   |\n10 |     let total: u32 = compute_everything();\n   |                      ^^^^^^^^^^^^^^^^^^^^ expected `u32`"],
            [("zoom", zoom), ("mod_menu", modMenu)]));
    }
}
