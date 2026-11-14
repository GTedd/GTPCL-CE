using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Horizon;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class HorizonReleaseFeedTest
{
    private const string Sha = "d9c83d7096b7ea5b83678da2efdfd6161f74997e";

    // 与 https://github.com/horizon-rust/horizon-mixins/commits/<sha>.atom 的结构一致（节选）
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/" xml:lang="en-US">
          <id>tag:github.com,2008:/horizon-rust/horizon-mixins/commits/d9c83d7096b7ea5b83678da2efdfd6161f74997e</id>
          <title>Recent Commits to horizon-mixins:d9c83d7096b7ea5b83678da2efdfd6161f74997e</title>
          <updated>2026-10-30T13:00:00Z</updated>
          <entry>
            <id>tag:github.com,2008:Grit::Commit/d9c83d7096b7ea5b83678da2efdfd6161f74997e</id>
            <title>Merge pull request #7</title>
            <updated>2026-10-30T12:35:13Z</updated>
          </entry>
          <entry>
            <id>tag:github.com,2008:Grit::Commit/f419c9c919e34862eff3d332d59cc1058380ad05</id>
            <updated>2026-10-30T12:35:04Z</updated>
          </entry>
        </feed>
        """;

    [TestMethod]
    public void FeedGivesFirstEntryShaAndCommitterDate()
    {
        var (sha, date) = HorizonRelease.ParseCommitFeed(Feed, Sha);
        Assert.AreEqual(Sha, sha);
        Assert.AreEqual("2026-10-30T12:35:13Z", date);
    }

    [TestMethod]
    public void FeedResolvesShortSha()
    {
        Assert.AreEqual(Sha, HorizonRelease.ParseCommitFeed(Feed, "d9c83d7").Sha);
    }

    [TestMethod]
    public void FeedDateIsNormalizedToUtc()
    {
        var feed = Feed.Replace("<updated>2026-10-30T12:35:13Z</updated>", "<updated>2026-10-30T13:35:13+01:00</updated>");
        Assert.AreEqual("2026-10-30T12:35:13Z", HorizonRelease.ParseCommitFeed(feed, Sha).Date);
    }

    [TestMethod]
    public void FeedForAnotherCommitIsRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonRelease.ParseCommitFeed(Feed, "5a64af938ec86b00d53d16a408aabc0038fb3e57"));
    }

    [TestMethod]
    public void EmptyFeedIsRejected()
    {
        const string empty = """<feed xmlns="http://www.w3.org/2005/Atom"><title>x</title></feed>""";
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonRelease.ParseCommitFeed(empty, Sha));
    }
}
