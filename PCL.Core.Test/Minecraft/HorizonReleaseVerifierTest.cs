using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Horizon;

namespace PCL.Test.Minecraft;

/// <summary>
///     用 Horizon 真实发布的 Nightly 2026-11-13 · 9f42064 的 SHA256SUMS 与 SHA256SUMS.v2.sig 校验。
/// </summary>
[TestClass]
public sealed class HorizonReleaseVerifierTest
{
    private const string Version = "nightly-2026-11-13-9f42064";

    private const string Sums =
        "83d36c5a2f8b6690d4d77411dfc3f85753319184fffe738ee5f0a4c552a0eabe  horizon-linux-x86_64.tar.gz\n" +
        "f47f9f988b7c30b727dfb35aed2b74f01428925b2eccc9bafff2352f4847f7d6  horizon-windows-x64.zip\n" +
        "9cc4b7a72afba2df2a2c729f76a983d1e6cb82de44d1474fca04bc4d2a40d553  horizon-macos-arm64.zip\n" +
        "f7d2d17d488ab2111fa0e7ce36a033fe6eb6db3ee6b74bc86aaa26dbab7e0144  horizon-macos-x64.zip\n" +
        "f621e334c254d56bfeed16ed989500a36ef82a6a2e84ee6b861adcb774ced9b5  horizon-server-linux-x86_64.tar.gz\n" +
        "aa9bf9383ae4e926d5f9c2ca8be997e843a6cf590fc3c8c04162a26718adeabd  horizon-source.tar.gz\n";

    private const string Signature =
        "W0WzX99BwYl5heDJXYjySOlYUWfQUSXVPll9VhrOwKehaHXbffIDAPpWG6U53k7aBfYXKD/FmJmBfjRiAioFCw==\n";

    [TestMethod]
    public void NightlyTitle_GivesSignedVersion()
    {
        Assert.AreEqual(Version, HorizonReleaseVerifier.NightlyVersionFromTitle("Nightly 2026-11-13 · 9f42064"));
        Assert.IsNull(HorizonReleaseVerifier.NightlyVersionFromTitle("Nightly 2026-13-40 · 9f42064"));
        Assert.IsNull(HorizonReleaseVerifier.NightlyVersionFromTitle("Horizon 0.2.97"));
    }

    [TestMethod]
    public void SignedSums_GiveTheAssetDigest()
    {
        var digest = HorizonReleaseVerifier.SignedDigest(
            Version, Encoding.UTF8.GetBytes(Sums), Signature, "horizon-windows-x64.zip");
        Assert.AreEqual("f47f9f988b7c30b727dfb35aed2b74f01428925b2eccc9bafff2352f4847f7d6", digest);
    }

    [TestMethod]
    public void SignatureForAnotherVersion_IsRefused()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonReleaseVerifier.SignedDigest(
            "nightly-2026-11-12-9f42064", Encoding.UTF8.GetBytes(Sums), Signature, "horizon-windows-x64.zip"));
    }

    [TestMethod]
    public void TamperedSums_AreRefused()
    {
        var tampered = Sums.Replace("f47f9f98", "00000000");
        Assert.ThrowsExactly<InvalidDataException>(() => HorizonReleaseVerifier.SignedDigest(
            Version, Encoding.UTF8.GetBytes(tampered), Signature, "horizon-windows-x64.zip"));
    }
}
