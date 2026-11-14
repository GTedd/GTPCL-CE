using System;

namespace PCL.Core.Link.McPing;

/// <summary>
/// 服务器探测失败的类别，决定上层是否值得换一种协议重试。
/// </summary>
public enum McPingFailureKind
{
    /// <summary>无法建立连接（拒绝连接、DNS 失败、网络不可达）。换协议重试没有意义。</summary>
    Unreachable,

    /// <summary>连接或读写超时。</summary>
    Timeout,

    /// <summary>连接已建立，但服务器在返回状态前断开或重置了连接。</summary>
    ConnectionClosed,

    /// <summary>服务器返回了无法识别的数据。</summary>
    InvalidResponse
}

/// <summary>
/// 服务器探测失败。<see cref="Exception.Message" /> 是可以直接展示给用户的原因。
/// </summary>
public sealed class McPingException(McPingFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public McPingFailureKind Kind { get; } = kind;

    /// <summary>连接已建立但协议交互失败，换一种握手方式可能成功。</summary>
    public bool IsProtocolFailure => Kind is McPingFailureKind.ConnectionClosed or McPingFailureKind.InvalidResponse;
}
