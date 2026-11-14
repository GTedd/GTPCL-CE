using System;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     minisign 签名校验，用于 Horizon 模组注册表中带 <c>key</c> 的项目（docs/api/mod-index.md）。
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>公钥：base64 的 42 字节 = 算法 <c>Ed</c>（2）+ 密钥 ID（8）+ Ed25519 公钥（32）。</item>
///         <item>签名文本：第 1 行 <c>untrusted comment:</c>；第 2 行 base64 的 74 字节 = 算法（2）+ 密钥 ID（8）+ 签名（64）；
///             第 3 行 <c>trusted comment: …</c>；第 4 行 base64 的 64 字节全局签名。</item>
///         <item>算法 <c>ED</c> 为预哈希：对文件的 BLAKE2b-512 签名；<c>Ed</c> 为旧式：对文件本身签名。</item>
///         <item>全局签名覆盖“签名 64 字节 + 可信注释文本”，保证可信注释没被改过。</item>
///     </list>
/// </remarks>
public static class HorizonMinisign
{
    private static readonly byte[] _LegacyAlgorithm = "Ed"u8.ToArray();
    private static readonly byte[] _PrehashedAlgorithm = "ED"u8.ToArray();
    private const string TrustedCommentPrefix = "trusted comment: ";

    public sealed record PublicKey(byte[] KeyId, byte[] Key);

    public sealed record Signature(bool Prehashed, byte[] KeyId, byte[] Value, string TrustedComment, byte[] GlobalSignature);

    /// <exception cref="FormatException">公钥格式无效。</exception>
    public static PublicKey ParsePublicKey(string base64)
    {
        var bytes = _Base64(base64.Trim(), "key");
        if (bytes.Length != 42 || !bytes.AsSpan(0, 2).SequenceEqual(_LegacyAlgorithm))
            throw new FormatException("key: not a minisign Ed25519 public key");
        return new PublicKey(bytes[2..10], bytes[10..42]);
    }

    /// <exception cref="FormatException">签名格式无效。</exception>
    public static Signature ParseSignature(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 4) throw new FormatException("signature: expected 4 lines");
        if (!lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal))
            throw new FormatException("signature: missing untrusted comment");
        var sig = _Base64(lines[1].Trim(), "signature");
        if (sig.Length != 74) throw new FormatException("signature: wrong length");
        bool prehashed;
        if (sig.AsSpan(0, 2).SequenceEqual(_PrehashedAlgorithm)) prehashed = true;
        else if (sig.AsSpan(0, 2).SequenceEqual(_LegacyAlgorithm)) prehashed = false;
        else throw new FormatException("signature: unknown algorithm");
        if (!lines[2].StartsWith(TrustedCommentPrefix, StringComparison.Ordinal))
            throw new FormatException("signature: missing trusted comment");
        var global = _Base64(lines[3].Trim(), "signature");
        if (global.Length != 64) throw new FormatException("signature: wrong global signature length");
        return new Signature(prehashed, sig[2..10], sig[10..74], lines[2][TrustedCommentPrefix.Length..], global);
    }

    /// <summary>
    ///     校验文件的 minisign 签名。格式错误抛出 <see cref="FormatException" />，签名不符返回 false。
    /// </summary>
    public static bool VerifyFile(string publicKeyBase64, string signatureText, string path)
    {
        var key = ParsePublicKey(publicKeyBase64);
        var signature = ParseSignature(signatureText);
        if (!key.KeyId.SequenceEqual(signature.KeyId)) return false;

        byte[] message;
        if (signature.Prehashed)
        {
            var digest = new Blake2bDigest(512);
            var buffer = new byte[64 * 1024];
            using var stream = File.OpenRead(path);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) digest.BlockUpdate(buffer, 0, read);
            message = new byte[64];
            digest.DoFinal(message, 0);
        }
        else
        {
            message = File.ReadAllBytes(path);
        }

        if (!_Ed25519Verify(key.Key, message, signature.Value)) return false;
        var global = signature.Value.Concat(Encoding.UTF8.GetBytes(signature.TrustedComment)).ToArray();
        return _Ed25519Verify(key.Key, global, signature.GlobalSignature);
    }

    private static bool _Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static byte[] _Base64(string text, string what)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"{what}: invalid base64", ex);
        }
    }
}
