using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using PCL.Core.Minecraft.Horizon;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class HorizonNativeAndPacksTest
{
    private const string Revision = "9f42064aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcl-horizon-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // ===== minisign =====

    [TestMethod]
    public void MinisignVerifiesPrehashedSignature()
    {
        var file = Path.Combine(_dir, "mod.hnmod");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("pretend this is a mod"));
        var (key, signature) = _Sign(file, "timestamp:1\tfile:mod.hnmod\thashed");

        Assert.IsTrue(HorizonMinisign.VerifyFile(key, signature, file));

        File.AppendAllText(file, "tampered");
        Assert.IsFalse(HorizonMinisign.VerifyFile(key, signature, file));
    }

    [TestMethod]
    public void MinisignRejectsEditedTrustedComment()
    {
        var file = Path.Combine(_dir, "mod.hnmod");
        File.WriteAllText(file, "content");
        var (key, signature) = _Sign(file, "timestamp:1");
        var edited = signature.Replace("trusted comment: timestamp:1", "trusted comment: timestamp:2");
        Assert.IsFalse(HorizonMinisign.VerifyFile(key, edited, file));
        Assert.ThrowsExactly<FormatException>(() => HorizonMinisign.ParsePublicKey("not-base64!"));
    }

    // ===== 原生构建 =====

    [TestMethod]
    public void RepinReplacesOrAddsRevision()
    {
        const string pinned = "[mod]\nid = \"zoom\"\n\n[compatibility]\nhorizon_revision = \"old\"\n\n[[injection]]\nid = \"a\"\n";
        var repinned = HorizonNativeBundle.Repin(pinned, Revision);
        Assert.AreEqual(Revision, HorizonToml.GetString(HorizonToml.Parse(repinned), "compatibility.horizon_revision"));
        Assert.AreEqual("a", ((List<object?>)HorizonToml.Parse(repinned)["injection"]!).Cast<Dictionary<string, object?>>().Single()["id"]);

        var unpinned = HorizonNativeBundle.Repin("[mod]\nid = \"zoom\"\n", Revision);
        Assert.AreEqual(Revision, HorizonToml.GetString(HorizonToml.Parse(unpinned), "compatibility.horizon_revision"));
    }

    [TestMethod]
    public void VersionNameDependsOnEveryInput()
    {
        Dictionary<string, byte[]> Files(string body) => new() { ["horizon-mixin.toml"] = Encoding.UTF8.GetBytes(body) };

        var a = HorizonNativeLogic.VersionName("m", "fast", [], Revision, [("zoom", Files("x"))]);
        Assert.AreEqual(a, HorizonNativeLogic.VersionName("m", "fast", [], Revision, [("zoom", Files("x"))]));
        StringAssert.StartsWith(a, "native-9f42064-");
        Assert.AreEqual("native-9f42064-".Length + 12, a.Length);
        Assert.AreNotEqual(a, HorizonNativeLogic.VersionName("m", "release", [], Revision, [("zoom", Files("x"))]));
        Assert.AreNotEqual(a, HorizonNativeLogic.VersionName("m", "fast", [], Revision, [("zoom", Files("y"))]));
        Assert.AreNotEqual(a, HorizonNativeLogic.VersionName("n", "fast", [], Revision, [("zoom", Files("x"))]));
    }

    [TestMethod]
    public void StateComparesModsAndDigests()
    {
        var record = new HorizonNativeRecord
        {
            Base = "nightly", Horizon = Revision, HorizonMixins = Revision,
            Mods = [new HorizonNativeBuiltMod("zoom", "2.0.0", "aa")],
            LeftOut = [new HorizonNativeLeftOut("old", "1.0", "other-version", null)]
        };
        Assert.AreEqual(HorizonNativeState.NoMods, HorizonNativeLogic.State([], record));
        Assert.AreEqual(HorizonNativeState.NotBuilt, HorizonNativeLogic.State([new("zoom", "2.0.0", "aa")], null));
        Assert.AreEqual(HorizonNativeState.UpToDate,
            HorizonNativeLogic.State([new("zoom", "2.0.0", "aa"), new("old", "1.0", "bb")], record));
        Assert.AreEqual(HorizonNativeState.NeedsRebuild, HorizonNativeLogic.State([new("zoom", "2.0.0", "aa")], record));
        Assert.AreEqual(HorizonNativeState.NeedsRebuild,
            HorizonNativeLogic.State([new("zoom", "2.0.0", "cc"), new("old", "1.0", null)], record));

        var path = Path.Combine(_dir, HorizonNativeRecord.FileName);
        File.WriteAllText(path, record.ToJson());
        var read = HorizonNativeRecord.TryRead(path)!;
        Assert.AreEqual("zoom", read.Mods.Single().Id);
        Assert.AreEqual("other-version", read.LeftOut.Single().Why);
        StringAssert.StartsWith(record.ToJson(), "{\"base\":");
    }

    [TestMethod]
    public void SyncWritesOnlyChangedFiles()
    {
        var from = Path.Combine(_dir, "from");
        var to = Path.Combine(_dir, "to");
        Directory.CreateDirectory(Path.Combine(from, "src"));
        File.WriteAllText(Path.Combine(from, "src", "a.rs"), "a");
        File.WriteAllText(Path.Combine(from, "b.rs"), "b");
        Assert.AreEqual(2, HorizonNativeLogic.Sync(from, to));

        var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(to, "b.rs"), old);
        File.WriteAllText(Path.Combine(from, "src", "a.rs"), "changed");
        File.Delete(Path.Combine(from, "b.rs"));
        File.WriteAllText(Path.Combine(from, "c.rs"), "c");
        File.WriteAllText(Path.Combine(to, "b.rs"), "b");
        File.SetLastWriteTimeUtc(Path.Combine(to, "b.rs"), old);

        Assert.AreEqual(2, HorizonNativeLogic.Sync(from, to));
        Assert.AreEqual("changed", File.ReadAllText(Path.Combine(to, "src", "a.rs")));
        Assert.IsFalse(File.Exists(Path.Combine(to, "b.rs")));
        Assert.IsTrue(File.GetLastWriteTimeUtc(Path.Combine(to, "c.rs")) > DateTime.UtcNow.AddMinutes(-1));
    }

    [TestMethod]
    public void CargoProgressReadsBuildingBar()
    {
        var progress = new HorizonRustToolchain.CargoProgress();
        Assert.AreEqual("Compiling serde v1.0.0", progress.Feed("\u001b[1m\u001b[32m   Compiling\u001b[0m serde v1.0.0"));
        Assert.AreEqual("", progress.Feed("    Building [=====>      ] 120/384: serde, ring"));
        Assert.AreEqual(120, progress.Crates);
        Assert.AreEqual(384, progress.Total);
        Assert.IsTrue(HorizonRustToolchain.IsToolSafe(@"D:\.minecraft\horizon-native"));
        Assert.IsFalse(HorizonRustToolchain.IsToolSafe(@"D:\我的世界\PCL"));
        Assert.IsFalse(HorizonRustToolchain.IsToolSafe(@"C:\Program Files\x"));
    }

    // ===== 包 =====

    [TestMethod]
    public void ResourcePackToggleMatchesOfficialLauncher()
    {
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "options.txt"),
            "version:4786\nresourcePacks:[\"vanilla\",\"file/Zed.zip\"]\nincompatibleResourcePacks:[\"file/Zed.zip\"]\nlang:fr_ch\n");
        HorizonPack Pack(string file) => new(HorizonPackKind.ResourcePack, file, file, file, "", false, null, null);

        HorizonPacks.SetResourcePackEnabled(game, Pack("Glass.zip"), true);
        HorizonPacks.SetResourcePackEnabled(game, Pack("Folder Pack"), true);
        HorizonPacks.SetResourcePackEnabled(game, Pack("Zed.zip"), false);

        Assert.AreEqual(
            "version:4786\nresourcePacks:[\"vanilla\",\"file/Glass.zip\",\"file/Folder Pack\"]\nincompatibleResourcePacks:[]\nlang:fr_ch\n",
            File.ReadAllText(Path.Combine(game, "options.txt")));
    }

    [TestMethod]
    public void ShaderToggleWritesHorizonKey()
    {
        var game = Path.Combine(_dir, "game");
        var packs = Path.Combine(game, "shaderpacks");
        Directory.CreateDirectory(Path.Combine(packs, "Warm"));
        File.WriteAllText(Path.Combine(packs, "Warm", "horizon-shaders.toml"),
            "schema = 1\nid = \"warm\"\nname = \"Warm\"\nversion = \"1.0\"\napi = \"0.1\"\n[sky]\nfile = \"sky.wgsl\"\n");
        Directory.CreateDirectory(Path.Combine(packs, "Classic", "shaders"));
        File.WriteAllText(Path.Combine(packs, "Warm.txt"), "bloom=on\n");
        File.WriteAllText(Path.Combine(game, "horizon-graphics.txt"), "preset:custom\npickedShaderPack:\nlodDistance:96\n");

        var list = HorizonPacks.ListShaders(game);
        Assert.AreEqual(2, list.Count);
        var warm = list.Single(p => p.Title == "Warm");
        Assert.AreEqual("horizon:warm", warm.ShaderKey);

        HorizonPacks.SetShaderEnabled(game, warm, true);
        Assert.AreEqual("preset:custom\npickedShaderPack:horizon:warm\nlodDistance:96\n",
            File.ReadAllText(Path.Combine(game, "horizon-graphics.txt")));
        Assert.AreEqual("bloom=on\n", File.ReadAllText(Path.Combine(packs, ".horizon-options", "warm.txt")));
        Assert.IsTrue(HorizonPacks.ListShaders(game).Single(p => p.Title == "Warm").Enabled);

        HorizonPacks.SetShaderEnabled(game, list.Single(p => p.File == "Classic"), true);
        Assert.AreEqual("Classic", HorizonPacks.ReadLine(Path.Combine(game, "horizon-graphics.txt"), "pickedShaderPack"));
        HorizonPacks.SetShaderEnabled(game, warm, false);
        Assert.AreEqual("", HorizonPacks.ReadLine(Path.Combine(game, "horizon-graphics.txt"), "pickedShaderPack"));
    }

    [TestMethod]
    public void DataPackToggleUsesDatapacksJson()
    {
        var world = Path.Combine(_dir, "world");
        Directory.CreateDirectory(Path.Combine(world, "datapacks", "Trades"));
        File.WriteAllText(Path.Combine(world, "datapacks", "Trades", "pack.mcmeta"), "{\"pack\":{\"description\":\"§aTrades\",\"pack_format\":121}}");
        File.WriteAllText(Path.Combine(world, "datapacks.json"), "{\"enabled\":[\"vanilla\"],\"disabled\":[],\"extra\":1}");

        var pack = HorizonPacks.ListDataPacks(world).Single();
        Assert.IsTrue(pack.Enabled, "两个列表都没有的包是打开的");
        Assert.AreEqual("Trades", pack.Description);

        HorizonPacks.SetDataPackEnabled(world, pack, false);
        Assert.IsFalse(HorizonPacks.ListDataPacks(world).Single().Enabled);
        var json = File.ReadAllText(Path.Combine(world, "datapacks.json"));
        StringAssert.Contains(json, "\"extra\": 1");
        StringAssert.Contains(json, "file/Trades");

        HorizonPacks.SetDataPackEnabled(world, pack, true);
        Assert.IsTrue(HorizonPacks.ListDataPacks(world).Single().Enabled);
    }

    /// <summary>按 minisign 格式（ED 预哈希）生成密钥和签名。</summary>
    private static (string Key, string Signature) _Sign(string file, string trustedComment)
    {
        var random = new Org.BouncyCastle.Security.SecureRandom();
        var privateKey = new Ed25519PrivateKeyParameters(random);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        var keyId = new byte[8];
        random.NextBytes(keyId);

        var digest = new Blake2bDigest(512);
        var bytes = File.ReadAllBytes(file);
        digest.BlockUpdate(bytes, 0, bytes.Length);
        var hash = new byte[64];
        digest.DoFinal(hash, 0);

        byte[] Sign(byte[] message)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }

        var signature = Sign(hash);
        var global = Sign(signature.Concat(Encoding.UTF8.GetBytes(trustedComment)).ToArray());
        var key = Convert.ToBase64String("Ed"u8.ToArray().Concat(keyId).Concat(publicKey).ToArray());
        var sig = Convert.ToBase64String("ED"u8.ToArray().Concat(keyId).Concat(signature).ToArray());
        return (key,
            $"untrusted comment: test\n{sig}\ntrusted comment: {trustedComment}\n{Convert.ToBase64String(global)}\n");
    }
}
