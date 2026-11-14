using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net;

namespace PCL.Core.Test.IO;

[TestClass]
public class TextTranslatorTest
{
    [TestMethod]
    public void SplitKeepsLinesAndLimit()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 400; i++) builder.Append("- Fixed item number ").Append(i).Append(" in the changelog\n");
        var text = builder.ToString().TrimEnd('\n');

        var chunks = TextTranslator.Split(text, 1000);

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(chunk => chunk.Length <= 1000));
        Assert.AreEqual(text, string.Join("\n", chunks));
        Assert.IsTrue(chunks.All(chunk => chunk.StartsWith("- Fixed item")), "段落应从行首切开");
    }

    [TestMethod]
    public void SplitCutsAnOverlongLine()
    {
        var line = new string('a', 2500);
        var chunks = TextTranslator.Split("head\n" + line + "\ntail", 1000);
        CollectionAssert.AreEqual(new[] { "head", new string('a', 1000), new string('a', 1000), new string('a', 500), "tail" }, chunks);
    }

    [TestMethod]
    public void BingPageGivesSession()
    {
        const string html = "<div id=\"rich_tta\" data-iid=\"translator.5023\"></div><script>_G={IG:\"EB4C43B45AA44168987E9C732E667530\"};" +
                            "var params_AbusePreventionHelper = [1794676193162,\"986ytI-0fgeAWgsObblWLjKnCoQdsT6o\",3600000];</script>";
        var now = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        var session = TextTranslator.ParseBingPage(html, now);
        Assert.IsNotNull(session);
        Assert.AreEqual("EB4C43B45AA44168987E9C732E667530", session.Ig);
        Assert.AreEqual("translator.5023", session.Iid);
        Assert.AreEqual("1794676193162", session.Key);
        Assert.AreEqual("986ytI-0fgeAWgsObblWLjKnCoQdsT6o", session.Token);
        Assert.AreEqual(now.AddMinutes(55), session.Expires);
        Assert.IsNull(TextTranslator.ParseBingPage("<html></html>", now));
    }

    [TestMethod]
    public void BingResultIsRead()
    {
        Assert.AreEqual("### 修复\n- 修好了",
            TextTranslator.ParseBingResult("[{\"detectedLanguage\":{\"language\":\"en\"},\"translations\":[{\"text\":\"### 修复\\n- 修好了\",\"to\":\"zh-Hans\"}]}]"));
        Assert.ThrowsExactly<InvalidDataException>(() => TextTranslator.ParseBingResult("{\"statusCode\":400,\"errorMessage\":\"\"}"));
    }

    [TestMethod]
    public void GoogleResultJoinsSentences()
    {
        Assert.AreEqual("### 已修复\n- 停止冲刺",
            TextTranslator.ParseGoogleResult("[[[\"### 已修复\\n\",\"### Fixed\\n\",null,null,3],[\"- 停止冲刺\",\"- Stop sprint\",null,null,3]],null,\"en\"]"));
        Assert.ThrowsExactly<InvalidDataException>(() => TextTranslator.ParseGoogleResult("{}"));
    }
}
