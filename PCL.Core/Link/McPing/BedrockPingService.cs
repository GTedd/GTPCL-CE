using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Localization;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Logging;

namespace PCL.Core.Link.McPing;

/// <summary>
/// 基岩版服务器探测（RakNet Unconnected Ping / Pong，UDP）。
/// </summary>
/// <remarks>
/// UDP 可能丢包，所以在超时之前每隔一段时间重发一次 Ping，收到第一个合法的 Pong 即返回。
/// Pong 中的广播字符串格式：<c>MCPE;MOTD;协议号;版本;在线;最大;服务器 ID;副标题;游戏模式;…</c>。
/// </remarks>
public sealed partial class BedrockPingService : IMcPingService
{
    public const int DefaultPort = 19132;

    private const int DefaultTimeout = 3000;
    private const byte UnconnectedPingId = 0x01;
    private const byte UnconnectedPongId = 0x1C;
    private const int PongHeaderLength = 1 + 8 + 8 + 16 + 2;
    private const string ModuleName = "BedrockPing";
    private static readonly TimeSpan _ResendInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>RakNet 的离线消息标识（OFFLINE_MESSAGE_DATA_ID）。</summary>
    private static readonly byte[] _OfflineMessageDataId =
        [0x00, 0xFF, 0xFF, 0x00, 0xFE, 0xFE, 0xFE, 0xFE, 0xFD, 0xFD, 0xFD, 0xFD, 0x12, 0x34, 0x56, 0x78];

    private static readonly long _ClientGuid = Random.Shared.NextInt64();

    /// <summary>基岩版独有的“材质色”代码，渲染器不认识，换成对应的十六进制颜色。</summary>
    private static readonly Dictionary<char, string> _BedrockOnlyColors = new()
    {
        ['g'] = "#DDD605", // minecoin_gold
        ['h'] = "#E3D4D1", // material_quartz
        ['i'] = "#CECACA", // material_iron
        ['j'] = "#443A3B", // material_netherite
        ['m'] = "#971607", // material_redstone（基岩版没有删除线）
        ['n'] = "#B4684D", // material_copper（基岩版没有下划线）
        ['p'] = "#DEB12D", // material_gold
        ['q'] = "#47A036", // material_emerald
        ['s'] = "#2CBAA8", // material_diamond
        ['t'] = "#21497B", // material_lapis
        ['u'] = "#9A5CC6", // material_amethyst
        ['v'] = "#EB7114" // material_resin
    };

    private readonly IPEndPoint _endpoint;
    private readonly string _host;
    private readonly int _timeout;
    private bool _disposed;

    public IPEndPoint Endpoint => _endpoint;
    public string Host => _host;
    public int Timeout => _timeout;

    public BedrockPingService(IPEndPoint endpoint, int timeout = DefaultTimeout)
        : this(endpoint.Address.ToString(), endpoint, timeout)
    {
    }

    public BedrockPingService(string host, IPEndPoint endpoint, int timeout = DefaultTimeout)
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

        using var socket = new Socket(_endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var buffer = new byte[4096];
        var stopwatch = Stopwatch.StartNew();
        Task<int>? receiveTask = null;

        try
        {
            // 对 UDP 来说 Connect 只是绑定默认对端：只接收来自该地址的数据，并且端口不可达时接收会立即报错
            socket.Connect(_endpoint);
            LogWrapper.Debug(ModuleName, $"Pinging {_endpoint} (host: {_host})");

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await socket.SendAsync(BuildPingPacket(stopwatch.ElapsedMilliseconds), SocketFlags.None, token)
                    .ConfigureAwait(false);
                var sentAt = stopwatch.ElapsedMilliseconds;

                receiveTask ??= socket.ReceiveAsync(buffer, SocketFlags.None, token).AsTask();
                var completed = await Task.WhenAny(receiveTask, Task.Delay(_ResendInterval, token)).ConfigureAwait(false);
                if (completed != receiveTask) continue;

                var length = await receiveTask.ConfigureAwait(false);
                receiveTask = null;
                if (!TryParsePong(buffer.AsSpan(0, length), out var pingTime, out var advertisement)) continue;

                var now = stopwatch.ElapsedMilliseconds;
                // 优先用 Pong 回显的发送时间计算往返；回显异常时退回最近一次发送的时间
                var latency = pingTime >= 0 && pingTime <= now ? now - pingTime : now - sentAt;
                var result = ParseAdvertisement(advertisement) with { Latency = latency };
                LogWrapper.Debug(ModuleName,
                    $"Pong received from {_endpoint}: {result.Version.Name} ({result.Version.Protocol}), " +
                    $"{result.Players.Online}/{result.Players.Max}, latency {latency}ms");
                return result;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McPingException(McPingFailureKind.Timeout, Lang.Text("Tools.ServerQuery.Error.Timeout.ReadWrite"));
        }
        catch (SocketException ex)
        {
            // ConnectionReset：收到 ICMP 端口不可达，说明该端口没有基岩版服务器
            throw new McPingException(McPingFailureKind.Unreachable, ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            throw new McPingException(McPingFailureKind.InvalidResponse, ex.Message, ex);
        }
        finally
        {
            // 退出时可能还有一个未完成的接收，关闭套接字会让它以异常结束，这里显式观察掉
            receiveTask?.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>构造 Unconnected Ping：ID + 时间（8）+ 离线消息标识（16）+ 客户端 GUID（8）。</summary>
    public static byte[] BuildPingPacket(long time)
    {
        var packet = new byte[1 + 8 + 16 + 8];
        packet[0] = UnconnectedPingId;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1), time);
        _OfflineMessageDataId.CopyTo(packet, 9);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(25), _ClientGuid);
        return packet;
    }

    /// <summary>
    /// 解析 Unconnected Pong：ID + 时间（8）+ 服务器 GUID（8）+ 离线消息标识（16）+ 字符串长度（2）+ 广播字符串。
    /// </summary>
    public static bool TryParsePong(ReadOnlySpan<byte> packet, out long pingTime, out string advertisement)
    {
        pingTime = 0;
        advertisement = string.Empty;
        if (packet.Length < PongHeaderLength || packet[0] != UnconnectedPongId) return false;
        if (!packet.Slice(17, 16).SequenceEqual(_OfflineMessageDataId)) return false;

        pingTime = BinaryPrimitives.ReadInt64BigEndian(packet[1..]);
        // 部分服务端写的长度与实际不符，以实际收到的字节为准
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(packet[33..]), packet.Length - PongHeaderLength);
        advertisement = Encoding.UTF8.GetString(packet.Slice(PongHeaderLength, length));
        return true;
    }

    /// <summary>
    /// 解析 Pong 中的广播字符串。
    /// </summary>
    /// <exception cref="InvalidDataException">字段不足。</exception>
    public static McPingResult ParseAdvertisement(string advertisement)
    {
        var parts = advertisement.Split(';');
        if (parts.Length < 6) throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));

        var motd = NormalizeBedrockText(parts[1]);
        var subMotd = parts.Length > 7 ? NormalizeBedrockText(parts[7]) : string.Empty;
        var description = string.IsNullOrWhiteSpace(subMotd) ? motd : $"{motd}\n{subMotd}";

        return new McPingResult(
            new McPingVersionResult(parts[3], _ParseInt(parts[2]) ?? -1),
            new McPingPlayerResult(_ParseInt(parts[5]) ?? 0, _ParseInt(parts[4]) ?? 0, null),
            description,
            null,
            0,
            null,
            null) { Edition = McServerEdition.Bedrock };
    }

    /// <summary>把基岩版独有的颜色代码换成渲染器能识别的十六进制颜色。</summary>
    public static string NormalizeBedrockText(string text) =>
        string.IsNullOrEmpty(text) || !text.Contains('§')
            ? text
            : _BedrockOnlyColorCode().Replace(text, match => _BedrockOnlyColors[match.Groups[1].Value[0]]);

    private static int? _ParseInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex("§([ghijmnpqstuv])")]
    private static partial Regex _BedrockOnlyColorCode();
}
