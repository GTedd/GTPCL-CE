using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Localization;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Logging;
using PCL.Core.Utils;

namespace PCL.Core.Link.McPing;

/// <summary>
/// Java 版 1.7+ 服务器列表协议（Server List Ping）探测。
/// </summary>
/// <remarks>
/// 交互顺序与原版客户端一致：握手 + 状态请求 → 读完状态响应 → 再发 Ping 测延迟。
/// <para>
/// 不能在收到状态响应之前就把 Ping 一起发出去：Velocity 等代理是异步生成状态响应的，收到 Ping 后会立即回 Pong
/// 并断开连接，状态响应就再也发不出来了。
/// </para>
/// <para>
/// Pong 只用来测延迟。服务器不回 Pong 时改用状态请求的往返时间，不判定为失败。
/// </para>
/// </remarks>
public class McPingService : IMcPingService
{
    /// <summary>握手协议版本 -1：约定表示“只查询状态”，服务器按自身版本返回。</summary>
    public const int StatusProtocolVersion = -1;

    /// <summary>少数服务器不接受 -1 时改用的具体协议版本（1.21.8）。</summary>
    public const int FallbackProtocolVersion = 772;

    private const int DefaultTimeout = 10000;
    private const int MaxPacketLength = 8 * 1024 * 1024;
    private const int MaxHostLength = 255;
    private const int MaxSkippedPackets = 8;
    private const string ModuleName = "McPing";
    private static readonly TimeSpan _PongTimeout = TimeSpan.FromSeconds(3);

    private readonly IPEndPoint _endpoint;
    private readonly string _host;
    private readonly int _timeout;
    private bool _disposed;

    public IPEndPoint Endpoint => _endpoint;
    public string Host => _host;
    public int Timeout => _timeout;

    /// <summary>握手包中的协议版本号，默认 <see cref="StatusProtocolVersion" />。</summary>
    public int ProtocolVersion { get; init; } = StatusProtocolVersion;

    public McPingService(IPEndPoint endpoint, int timeout = DefaultTimeout)
    {
        _endpoint = endpoint;
        _host = _endpoint.Address.ToString();
        _timeout = timeout;
    }

    public McPingService(string ip, int port = 25565, int timeout = DefaultTimeout)
    {
        _endpoint = IPAddress.TryParse(ip, out var ipAddress)
            ? new IPEndPoint(ipAddress, port)
            : new IPEndPoint(Dns.GetHostAddresses(ip).First(), port);
        _host = ip;
        _timeout = timeout;
    }

    /// <param name="host">写入握手包的服务器地址。必须是玩家填写的原始域名而不是 SRV 目标，
    /// 否则 BungeeCord / Velocity / TCPShield 之类按域名路由的代理会认错服务器。</param>
    /// <param name="endpoint">实际连接的地址。</param>
    /// <param name="timeout">超时时间（毫秒）。</param>
    public McPingService(string host, IPEndPoint endpoint, int timeout = DefaultTimeout)
    {
        _endpoint = endpoint;
        _host = host;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<McPingResult?> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await QueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogWrapper.Debug(ModuleName, $"Ping {_endpoint} cancelled");
            return null;
        }
        catch (Exception ex)
        {
            LogWrapper.Info(ModuleName, $"Ping {_endpoint} failed: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<McPingResult> QueryAsync(CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        var handshake = _BuildHandshakePacket(_host, _endpoint.Port, ProtocolVersion);

        using var socket = new Socket(_endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;

        LogWrapper.Debug(ModuleName, $"Connecting to {_endpoint} (host: {_host}, protocol: {ProtocolVersion})");
        try
        {
            await socket.ConnectAsync(_endpoint, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McPingException(McPingFailureKind.Timeout, Lang.Text("Tools.ServerQuery.Error.Timeout.Connect"));
        }
        catch (SocketException ex)
        {
            throw new McPingException(McPingFailureKind.Unreachable, ex.Message, ex);
        }

        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using var reader = new BufferedStream(stream);

            // 握手与状态请求合并为一次写入，与原版客户端一致
            var request = new byte[handshake.Length + _StatusRequestPacket.Length];
            handshake.CopyTo(request, 0);
            _StatusRequestPacket.CopyTo(request, handshake.Length);

            var stopwatch = Stopwatch.StartNew();
            await stream.WriteAsync(request, token).ConfigureAwait(false);
            var json = await _ReadStatusResponseAsync(reader, token).ConfigureAwait(false);
            var statusRoundTrip = stopwatch.ElapsedMilliseconds;

            var result = McPingResponseParser.Parse(json);
            var latency = await _TryMeasureLatencyAsync(stream, reader, cancellationToken, token).ConfigureAwait(false);

            LogWrapper.Debug(ModuleName,
                $"Status received from {_endpoint}: {result.Version.Name} ({result.Version.Protocol}), " +
                $"{result.Players.Online}/{result.Players.Max}, latency {(latency is null ? $"~{statusRoundTrip}" : latency)}ms");
            return result with { Latency = latency ?? statusRoundTrip, Edition = McServerEdition.Java };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McPingException(McPingFailureKind.Timeout, Lang.Text("Tools.ServerQuery.Error.Timeout.ReadWrite"));
        }
        catch (Exception ex) when (McPingIo.TryTranslateReadFailure(ex, out var translated))
        {
            throw translated;
        }
        finally
        {
            McPingIo.ShutdownQuietly(socket);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 读取状态响应（包 ID 0x00）中的 JSON 文本。
    /// </summary>
    private static async Task<string> _ReadStatusResponseAsync(Stream stream, CancellationToken token)
    {
        for (var i = 0; i < MaxSkippedPackets; i++)
        {
            var (packetId, payload) = await _ReadPacketAsync(stream, token).ConfigureAwait(false);
            if (packetId != 0x00)
            {
                LogWrapper.Debug(ModuleName, $"Ignore unexpected packet 0x{packetId:X2} before status response");
                continue;
            }

            var offset = 0;
            var jsonLength = McPingIo.ReadVarInt(payload, ref offset);
            if (jsonLength < 0 || jsonLength > payload.Length - offset)
                throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));
            if (payload.Length - offset > jsonLength)
                LogWrapper.Debug(ModuleName, $"Status packet contains {payload.Length - offset - jsonLength} trailing bytes");

            return Encoding.UTF8.GetString(payload, offset, jsonLength);
        }

        throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));
    }

    /// <summary>
    /// 发送 Ping 并等待 Pong，返回往返毫秒数。服务器不回 Pong、提前断开或超时都返回 null。
    /// </summary>
    private async Task<long?> _TryMeasureLatencyAsync(
        Stream writer,
        Stream reader,
        CancellationToken callerToken,
        CancellationToken token)
    {
        using var pongCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        pongCts.CancelAfter(_PongTimeout);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await writer.WriteAsync(_BuildPingRequestPacket(Environment.TickCount64), pongCts.Token).ConfigureAwait(false);
            for (var i = 0; i < MaxSkippedPackets; i++)
            {
                var (packetId, _) = await _ReadPacketAsync(reader, pongCts.Token).ConfigureAwait(false);
                if (packetId == 0x01) return stopwatch.ElapsedMilliseconds;
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            LogWrapper.Debug(ModuleName, $"No pong from {_endpoint} in time, use status round trip as latency");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            LogWrapper.Debug(ModuleName, $"No pong from {_endpoint} ({ex.Message}), use status round trip as latency");
        }

        return null;
    }

    private static async Task<(int PacketId, byte[] Payload)> _ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var length = await McPingIo.ReadVarIntAsync(stream, token).ConfigureAwait(false);
        if (length == 0) throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.EmptyPacket"));
        if (length < 0 || length > MaxPacketLength)
            throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));

        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);

        var offset = 0;
        var packetId = McPingIo.ReadVarInt(data, ref offset);
        return (packetId, data[offset..]);
    }

    private static readonly byte[] _StatusRequestPacket = [0x01, 0x00];

    private static byte[] _BuildHandshakePacket(string host, int port, int protocolVersion)
    {
        // FQDN 末尾的点不属于握手地址
        host = host.TrimEnd('.');
        if (host.Length > MaxHostLength)
            throw new McPingException(McPingFailureKind.Unreachable, Lang.Text("Tools.ServerQuery.Error.AddressTooLong"));

        var hostBytes = Encoding.UTF8.GetBytes(host);
        using var body = new MemoryStream();
        body.Write(VarIntHelper.Encode(0u)); // 包 ID：握手
        body.Write(VarIntHelper.Encode((uint)protocolVersion)); // 负数按 32 位补码编码，-1 即 FF FF FF FF 0F
        body.Write(VarIntHelper.Encode((uint)hostBytes.Length)); // 必须是字节长度而不是字符长度
        body.Write(hostBytes);
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        body.Write(portBytes);
        body.Write(VarIntHelper.Encode(1u)); // 下一状态：1 = 状态查询

        return McPingIo.Frame(body.ToArray());
    }

    private static byte[] _BuildPingRequestPacket(long payload)
    {
        var packet = new byte[10];
        packet[0] = 9; // 包长度
        packet[1] = 0x01; // 包 ID：Ping
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2), payload);
        return packet;
    }
}

/// <summary>
/// 各探测实现共用的读写与异常归类工具。
/// </summary>
internal static class McPingIo
{
    public static byte[] Frame(byte[] body)
    {
        var length = VarIntHelper.Encode((uint)body.Length);
        var packet = new byte[length.Length + body.Length];
        length.CopyTo(packet, 0);
        body.CopyTo(packet, length.Length);
        return packet;
    }

    /// <summary>读取最多 5 字节的 32 位 VarInt。</summary>
    public static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            result |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0) return result;
        }

        throw new InvalidDataException("VarInt is too big");
    }

    public static int ReadVarInt(byte[] data, ref int offset)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("Incomplete VarInt");
            var b = data[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }

        throw new InvalidDataException("VarInt is too big");
    }

    /// <summary>
    /// 把连接建立之后的读写异常翻译为 <see cref="McPingException" />。无法归类的异常返回 false，原样抛出。
    /// </summary>
    public static bool TryTranslateReadFailure(Exception ex, out McPingException translated)
    {
        switch (ex)
        {
            case McPingException ping:
                translated = ping;
                return true;
            case EndOfStreamException:
                translated = new McPingException(McPingFailureKind.ConnectionClosed,
                    Lang.Text("Tools.ServerQuery.Error.IncompleteConnection"), ex);
                return true;
            case IOException { InnerException: SocketException socketEx }:
                translated = new McPingException(McPingFailureKind.ConnectionClosed, socketEx.Message, ex);
                return true;
            case IOException or SocketException:
                translated = new McPingException(McPingFailureKind.ConnectionClosed, ex.Message, ex);
                return true;
            case InvalidDataException or FormatException or OverflowException or DecoderFallbackException:
                translated = new McPingException(McPingFailureKind.InvalidResponse,
                    Lang.Text("Tools.ServerQuery.Error.InvalidResponse"), ex);
                return true;
            default:
                translated = null!;
                return false;
        }
    }

    public static void ShutdownQuietly(Socket socket)
    {
        try
        {
            if (socket.Connected) socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // 连接已经被对端关闭
        }
    }
}
