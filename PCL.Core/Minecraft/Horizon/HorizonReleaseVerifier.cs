using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     Horizon 发布包的签名校验，按 Horizon 的 Launch API 指南实现
///     （docs/api/launch-guide.md 与 crates/horizon-release-sign）：
///     <c>SHA256SUMS</c> 列出每个压缩包的 SHA-256，签名文件每行一个 base64 Ed25519 签名，
///     签名内容为 <c>horizon release &lt;版本&gt;\n</c> 后接 <c>SHA256SUMS</c> 的原始字节。
///     Nightly 用 <c>SHA256SUMS.v2.sig</c>，版本取自发布标题（nightly-日期-提交）。
/// </summary>
public static partial class HorizonReleaseVerifier
{
    /// <summary>
    ///     Horizon 的发布公钥，来自 crates/horizon-release-sign/release-keys.txt。
    /// </summary>
    public static readonly IReadOnlyList<string> PublicKeys =
    [
        "MU4oiIQcCxkZvbwSjjXs7SySCuM6ywRQ3nGaJYZG0gw="
    ];

    public const string SumsFileName = "SHA256SUMS";
    public const string NightlySignatureFileName = "SHA256SUMS.v2.sig";

    [GeneratedRegex(@"^Nightly (\d{4}-\d{2}-\d{2}) · ([0-9a-f]{7,40})$")]
    private static partial Regex NightlyTitleRegex();

    /// <summary>
    ///     由 Nightly 的发布标题（如 <c>Nightly 2026-11-13 · 9f42064</c>）得到它签名所用的版本
    ///     （<c>nightly-2026-11-13-9f42064</c>）；标题格式不符时返回 null。
    /// </summary>
    public static string? NightlyVersionFromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var match = NightlyTitleRegex().Match(title.Trim());
        if (!match.Success) return null;
        if (!DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", out _)) return null;
        return $"nightly-{match.Groups[1].Value}-{match.Groups[2].Value}";
    }

    /// <summary>
    ///     确认 <paramref name="sums" /> 由 Horizon 的发布密钥为 <paramref name="signedVersion" /> 签名，
    ///     并返回其中 <paramref name="assetName" /> 的 SHA-256（小写十六进制）。校验失败时抛出异常。
    /// </summary>
    public static string SignedDigest(string signedVersion, byte[] sums, string signatureText, string assetName)
    {
        var message = Encoding.UTF8.GetBytes($"horizon release {signedVersion}\n").Concat(sums).ToArray();
        var signed = false;
        foreach (var line in signatureText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(line);
            }
            catch (FormatException)
            {
                continue;
            }

            if (PublicKeys.Any(key => _Verify(key, message, signature)))
            {
                signed = true;
                break;
            }
        }

        if (!signed)
            throw new InvalidDataException($"Horizon {signedVersion} 的签名与发布密钥不符，可能已被篡改，已拒绝安装。");

        var digest = ParseSums(Encoding.UTF8.GetString(sums))
            .FirstOrDefault(pair => pair.Name == assetName).Sha256;
        return digest ?? throw new InvalidDataException($"{assetName} 不在 Horizon {signedVersion} 的签名校验清单中，已拒绝安装。");
    }

    /// <summary>
    ///     解析 <c>sha256sum</c> 格式的清单：每行 <c>&lt;hex&gt;  &lt;文件名&gt;</c> 或 <c>&lt;hex&gt; *&lt;文件名&gt;</c>。
    /// </summary>
    public static List<(string Name, string Sha256)> ParseSums(string text)
    {
        var result = new List<(string Name, string Sha256)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var space = line.IndexOf(' ');
            if (space != 64) throw new InvalidDataException($"{SumsFileName} 格式错误：{line}");
            var hex = line[..64];
            var name = line[(space + 1)..];
            if (name.StartsWith(' ') || name.StartsWith('*')) name = name[1..];
            if (!hex.All(Uri.IsHexDigit) || name.Length == 0)
                throw new InvalidDataException($"{SumsFileName} 格式错误：{line}");
            result.Add((name, hex.ToLowerInvariant()));
        }

        return result;
    }

    /// <summary>
    ///     计算文件的 SHA-256（小写十六进制）。
    /// </summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool _Verify(string publicKeyBase64, byte[] message, byte[] signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromBase64String(publicKeyBase64), 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
