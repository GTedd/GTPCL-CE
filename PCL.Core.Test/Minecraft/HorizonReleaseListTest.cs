using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Horizon;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class HorizonReleaseListTest
{
    private static readonly string[] NoProxies = [];

    private static JsonObject Asset(string name, long size = 10) => new()
    {
        ["name"] = name,
        ["size"] = size,
        ["browser_download_url"] = $"https://github.com/horizon-rust/horizon/releases/download/x/{name}"
    };

    private static JsonObject Release(string tag, string name, string published, bool draft = false, bool archive = true,
        bool nightlySig = true, bool releaseSig = true, string body = "")
    {
        var assets = new JsonArray { Asset("SHA256SUMS", 1) };
        if (archive) assets.Add(Asset(HorizonRelease.AssetName, 1000));
        if (nightlySig) assets.Add(Asset("SHA256SUMS.v2.sig", 1));
        if (releaseSig) assets.Add(Asset("SHA256SUMS.sig", 1));
        return new JsonObject
        {
            ["tag_name"] = tag,
            ["name"] = name,
            ["draft"] = draft,
            ["published_at"] = published,
            ["body"] = body,
            ["assets"] = assets
        };
    }

    private static JsonArray Sample() =>
    [
        Release("launcher@0.2.64", "Horizon launcher 0.2.64", "2026-11-14T01:58:33Z"),
        Release("nightly", "Nightly 2026-11-13 · 9f42064", "2026-11-13T07:47:16Z", body: "### New\n- Studio"),
        Release("v0.2.47", "Horizon 0.2.47: Dedicated server and 4 more changes", "2026-10-24T16:35:44Z"),
        Release("nightly-2026-11-08", "Nightly 2026-11-08 · fdc87ae", "2026-11-08T15:14:22Z"),
        Release("v0.2.97", "Horizon 0.2.97: Prepare, signs and 16 more fixes", "2026-10-28T20:57:32Z"),
        Release("v0.2.46", "Horizon 0.2.46: no builds", "2026-10-24T12:14:47Z", archive: false),
        Release("nightly-2026-11-13", "Nightly 2026-11-13 · 9f42064", "2026-11-13T07:40:00Z"),
        Release("v0.2.98", "Horizon 0.2.98: draft", "2026-10-29T00:00:00Z", draft: true),
        Release("nightly-2026-11-07", "Nightly 2026-11-07 · 2ebf00a", "2026-11-07T18:34:22Z", nightlySig: false)
    ];

    [TestMethod]
    public void GameTagsMatchTheOfficialLauncher()
    {
        Assert.IsTrue(HorizonRelease.IsGameTag("nightly"));
        Assert.IsTrue(HorizonRelease.IsGameTag("nightly-2026-11-08"));
        Assert.IsTrue(HorizonRelease.IsGameTag("v0.2.97"));
        Assert.IsTrue(HorizonRelease.IsGameTag("v2026.10.22"));
        Assert.IsFalse(HorizonRelease.IsGameTag("launcher@0.2.64"));
        Assert.IsFalse(HorizonRelease.IsGameTag("launcher-v1"));
        Assert.IsFalse(HorizonRelease.IsGameTag("vnext"));
        Assert.IsFalse(HorizonRelease.IsGameTag(null));
    }

    [TestMethod]
    public void ListKeepsInstallableBuildsNewestFirst()
    {
        var builds = HorizonRelease.ParseBuilds(Sample(), NoProxies);
        CollectionAssert.AreEqual(
            new[] { "nightly", "nightly-2026-11-08", "v0.2.97", "v0.2.47" },
            builds.Select(b => b.Tag).ToArray());
    }

    [TestMethod]
    public void RollingNightlyWinsOverTheSameArchivedBuild()
    {
        var builds = HorizonRelease.ParseBuilds(Sample(), NoProxies);
        var latest = builds[0];
        Assert.AreEqual("nightly-2026-11-13-9f42064", latest.Version);
        Assert.AreEqual("nightly", latest.Tag);
        Assert.AreEqual(1, builds.Count(b => b.Version == latest.Version));
        Assert.AreEqual("### New\n- Studio", latest.Changelog);
    }

    [TestMethod]
    public void NightliesAndReleasesUseTheirOwnSignatures()
    {
        var builds = HorizonRelease.ParseBuilds(Sample(), NoProxies);
        var nightly = builds.Single(b => b.Tag == "nightly-2026-11-08");
        Assert.AreEqual("nightly-2026-11-08-fdc87ae", nightly.Version);
        Assert.AreEqual("SHA256SUMS.v2.sig", nightly.SignatureFileName);
        Assert.AreEqual("fdc87ae", nightly.Commit);
        Assert.IsTrue(nightly.IsNightly);
        Assert.AreEqual("Nightly 2026-11-08 · fdc87ae", nightly.DisplayName);
        Assert.AreEqual("", nightly.Summary);

        var release = builds.Single(b => b.Tag == "v0.2.97");
        Assert.AreEqual("v0.2.97", release.Version);
        Assert.AreEqual("SHA256SUMS.sig", release.SignatureFileName);
        Assert.AreEqual("v0.2.97", release.Commit);
        Assert.IsFalse(release.IsNightly);
        Assert.AreEqual("Horizon 0.2.97", release.DisplayName);
        Assert.AreEqual("Prepare, signs and 16 more fixes", release.Summary);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 20, 57, 32, TimeSpan.Zero), release.PublishedAt);
        Assert.AreEqual("https://github.com/horizon-rust/horizon/releases/tag/v0.2.97", release.ReleaseUrl);
    }

    [TestMethod]
    public void ReleasesSortByVersionNumberNotText()
    {
        JsonArray releases =
        [
            Release("v0.2.9", "Horizon 0.2.9: a", "2026-10-01T00:00:00Z"),
            Release("v0.2.10", "Horizon 0.2.10: b", "2026-10-02T00:00:00Z"),
            Release("v2026.10.22", "v2026.10.22", "2026-10-22T00:00:00Z")
        ];
        CollectionAssert.AreEqual(
            new[] { "v0.2.10", "v0.2.9", "v2026.10.22" },
            HorizonRelease.ParseBuilds(releases, NoProxies).Select(b => b.Tag).ToArray());
    }

    [TestMethod]
    public void DownloadsGoThroughProxiesFirst()
    {
        var build = HorizonRelease.ParseBuilds(Sample(), ["https://gh-proxy.org/"])[0];
        Assert.AreEqual(2, build.AssetUrls.Count);
        StringAssert.StartsWith(build.AssetUrls[0], "https://gh-proxy.org/https://github.com/");
        StringAssert.StartsWith(build.AssetUrls[1], "https://github.com/");
    }

    [TestMethod]
    public void ChangelogDropsBannerIssueNumbersAndThanks()
    {
        const string body = "<h1>Want to play?</h1><h1>Do NOT run the files below.</h1>\r\n\r\n" +
                            "Nightly 2026-11-13 · 9f42064: main (9f42064).\r\n\r\n" +
                            "### New\r\n- Studio: free camera (#11897, fixes #11872)\r\n- Launcher: data packs (#12088)\r\n\r\n" +
                            "### Fixed\r\n- Sprint fix (fixes #1)\r\n\r\n" +
                            "Thanks to @a, @b.\r\n\r\n---\r\nNot an official Minecraft product.";
        Assert.AreEqual(
            "Nightly 2026-11-13 · 9f42064: main (9f42064).\n\n### New\n- Studio: free camera\n- Launcher: data packs\n\n### Fixed\n- Sprint fix",
            HorizonChangelog.Clean(body));
        Assert.AreEqual("", HorizonChangelog.Clean("  "));
        Assert.AreEqual("> **Outdated.**\n\n### Fixed\n- a",
            HorizonChangelog.Clean("> **Outdated.**\n\n<h3>banner</h3>\n### Fixed\n- a\n\n### Contributors\n- x"));
    }
}
