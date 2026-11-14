using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
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

namespace PCL.Core.Link.McPing;

/// <summary>
/// Java 版 1.6 及以下的旧版服务器列表协议（0xFE）探测。
/// </summary>
/// <remarks>
/// 发送 1.6 格式的请求（<c>FE 01 FA "MC|PingHost" …</c>）：1.4 - 1.6 的服务器按完整格式响应，
/// 更早的服务器忽略多余字节、按 Beta 1.8 格式响应，1.7+ 的原版服务器与主流代理也兼容这种请求。
/// 响应是踢出包（0xFF）+ UTF-16BE 字符串。
/// </remarks>
public class LegacyMcPingService : IMcPingService
{
    private const int DefaultTimeout = 10000;
    private const byte LegacyProtocolVersion = 74; // 1.6.2
    private const int MaxHostLength = 255;
    private const string ModuleName = "LegacyMcPing";

    private readonly IPEndPoint _endpoint;
    private readonly string _host;
    private readonly int _timeout;
    private bool _disposed;

    public IPEndPoint Endpoint => _endpoint;
    public string Host => _host;
    public int Timeout => _timeout;

    public LegacyMcPingService(IPEndPoint endpoint, int timeout = DefaultTimeout)
    {
        _endpoint = endpoint;
        _host = _endpoint.Address.ToString();
        _timeout = timeout;
    }

    public LegacyMcPingService(string ip, int port = 25565, int timeout = DefaultTimeout)
    {
        _endpoint = IPAddress.TryParse(ip, out var ipAddress)
            ? new IPEndPoint(ipAddress, port)
            : new IPEndPoint(Dns.GetHostAddresses(ip).First(), port);
        _host = ip;
        _timeout = timeout;
    }

    public LegacyMcPingService(string host, IPEndPoint endpoint, int timeout = DefaultTimeout)
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

        var request = BuildRequest(_host, _endpoint.Port);

        using var socket = new Socket(_endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;

        LogWrapper.Debug(ModuleName, $"Connecting to {_endpoint} (host: {_host})");
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

            var stopwatch = Stopwatch.StartNew();
            await stream.WriteAsync(request, token).ConfigureAwait(false);

            // 踢出包：0xFF + 字符数（short）+ UTF-16BE 字符串
            var header = new byte[3];
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var latency = stopwatch.ElapsedMilliseconds;
            if (header[0] != 0xFF)
                throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));

            var charCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
            var body = new byte[charCount * 2];
            await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);

            var result = ParseResponse(Encoding.BigEndianUnicode.GetString(body)) with { Latency = latency };
            LogWrapper.Debug(ModuleName,
                $"Status received from {_endpoint}: {result.Version.Name} ({result.Version.Protocol}), " +
                $"{result.Players.Online}/{result.Players.Max}, latency {latency}ms");
            return result;
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

    /// <summary>
    /// 解析踢出包里的状态字符串。
    /// </summary>
    /// <param name="response">
    /// 1.4+：<c>§1\0协议号\0版本\0MOTD\0在线人数\0最大人数</c>；Beta 1.8 - 1.3：<c>MOTD§在线人数§最大人数</c>。
    /// </param>
    /// <exception cref="InvalidDataException">格式无法识别。</exception>
    public static McPingResult ParseResponse(string response)
    {
        string version, motd, online, max;
        var protocol = -1;

        if (response.StartsWith("§1\0", StringComparison.Ordinal))
        {
            var parts = response.Split('\0');
            if (parts.Length < 6) throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));
            if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) protocol = p;
            (version, motd, online, max) = (parts[2], parts[3], parts[4], parts[5]);
        }
        else
        {
            var parts = response.Split('§');
            if (parts.Length < 3) throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));
            (version, motd, online, max) = (string.Empty, string.Join('§', parts[..^2]), parts[^2], parts[^1]);
        }

        if (!int.TryParse(online, NumberStyles.Integer, CultureInfo.InvariantCulture, out var onlineCount) ||
            !int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxCount))
            throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));

        return new McPingResult(
            new McPingVersionResult(version, protocol),
            new McPingPlayerResult(maxCount, onlineCount, null),
            McPingResponseParser.NormalizeLegacyText(motd),
            null,
            0,
            null,
            null) { Edition = McServerEdition.JavaLegacy };
    }

    /// <summary>
    /// 构造 1.6 格式的旧版探测请求。
    /// </summary>
    public static byte[] BuildRequest(string host, int port)
    {
        host = host.TrimEnd('.');
        if (host.Length > MaxHostLength)
            throw new McPingException(McPingFailureKind.Unreachable, Lang.Text("Tools.ServerQuery.Error.AddressTooLong"));

        const string channel = "MC|PingHost";
        var hostBytes = Encoding.BigEndianUnicode.GetBytes(host);

        using var ms = new MemoryStream();
        ms.Write([0xFE, 0x01, 0xFA]);
        _WriteUInt16(ms, channel.Length);
        ms.Write(Encoding.BigEndianUnicode.GetBytes(channel));
        _WriteUInt16(ms, 7 + hostBytes.Length); // 协议号 1 + 地址长度 2 + 地址 + 端口 4
        ms.WriteByte(LegacyProtocolVersion);
        _WriteUInt16(ms, host.Length);
        ms.Write(hostBytes);
        Span<byte> portBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(portBytes, port);
        ms.Write(portBytes);
        return ms.ToArray();
    }

    private static void _WriteUInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
