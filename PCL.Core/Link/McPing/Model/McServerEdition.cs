namespace PCL.Core.Link.McPing.Model;

/// <summary>
/// 服务器探测结果来自哪种协议。
/// </summary>
public enum McServerEdition
{
    /// <summary>Java 版 1.7+ 的 Server List Ping。</summary>
    Java,

    /// <summary>Java 版 1.6 及以下的旧版 Server List Ping（0xFE）。</summary>
    JavaLegacy,

    /// <summary>基岩版 RakNet Unconnected Ping。</summary>
    Bedrock
}
