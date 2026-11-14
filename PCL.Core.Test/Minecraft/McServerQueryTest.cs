using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.McPing;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Logging;

namespace PCL.Core.Test.Minecraft;

/// <summary>
/// 用本地假服务器验证服务器状态查询的协议交互，不依赖外网。
/// </summary>
[TestClass]
public class McServerQueryTest
{
    private const string StatusJson =
        """
        {"version":{"name":"Velocity 3.4.0","protocol":772},
         "players":{"max":100,"online":7,"sample":[{"name":"Steve","id":"8667ba71-b85a-4004-af54-457a9734eed7"}]},
         "description":{"text":"Proxy ","color":"aqua","extra":[{"text":"MOTD","bold":true}]},
         "favicon":"data:image/png;base64,AAAA"}
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CaptureLogs() => LogWrapper.OnLog += _WriteLog;

    [TestCleanup]
    public void ReleaseLogs() => LogWrapper.OnLog -= _WriteLog;

    private static void _WriteLog(LogLevel level, string msg, string? module, Exception? ex) =>
        Console.WriteLine($"[{level}] [{module}] {msg}{(ex is null ? "" : $" ({ex.GetType().Name}: {ex.Message})")}");

    // ===== Java 版现代协议 =====

    [TestMethod]
    public async Task PipelinedPingMakesVelocityLikeProxyDropStatus()
    {
        // 复现旧实现的问题：握手、状态请求、Ping 一起发出时，异步生成状态的代理先回 Pong 就断开
        await using var server = new FakeTcpServer(_HandleVelocityLikeAsync);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, TestContext.CancellationToken);
        var stream = client.GetStream();

        var request = _BuildHandshake(-1, "localhost", server.Port)
            .Concat(_Frame(0x00, []))
            .Concat(_Frame(0x01, new byte[8]))
            .ToArray();
        await stream.WriteAsync(request, TestContext.CancellationToken);

        var first = await _ReadFrameAsync(stream, TestContext.CancellationToken);
        Assert.AreEqual(0x01, first[0], "代理应先回 Pong");
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(
            async () => await _ReadFrameAsync(stream, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task QueryWaitsForStatusBeforePingAgainstVelocityLikeProxy()
    {
        HandshakeInfo? handshake = null;
        await using var server = new FakeTcpServer((stream, ct) => _HandleVelocityLikeAsync(stream, ct, h => handshake = h));

        using var service = new McPingService("play.example.com", new IPEndPoint(IPAddress.Loopback, server.Port), 5000);
        var result = await service.QueryAsync(TestContext.CancellationToken);

        Assert.AreEqual(McServerEdition.Java, result.Edition);
        Assert.AreEqual("Velocity 3.4.0", result.Version.Name);
        Assert.AreEqual(772, result.Version.Protocol);
        Assert.AreEqual(7, result.Players.Online);
        Assert.AreEqual(100, result.Players.Max);
        Assert.AreEqual("Steve", result.Players.Samples!.Single().Name);
        Assert.AreEqual("§bProxy §b§lMOTD", result.Description);
        Assert.AreEqual("data:image/png;base64,AAAA", result.Favicon);

        Assert.IsNotNull(handshake);
        Assert.AreEqual(-1, handshake.ProtocolVersion, "握手协议号应为 -1");
        Assert.AreEqual("play.example.com", handshake.Host, "握手地址应为玩家填写的原始域名");
        Assert.AreEqual(server.Port, handshake.Port);
        Assert.AreEqual(1, handshake.NextState);
    }

    [TestMethod]
    public async Task MissingPongFallsBackToStatusRoundTrip()
    {
        await using var server = new FakeTcpServer(async (stream, ct) =>
        {
            await _ReadFrameAsync(stream, ct); // 握手
            await _ReadFrameAsync(stream, ct); // 状态请求
            await stream.WriteAsync(_StatusResponse(StatusJson), ct);
            // 不回 Pong，直接断开
        });

        using var service = new McPingService("localhost", new IPEndPoint(IPAddress.Loopback, server.Port), 5000);
        var result = await service.QueryAsync(TestContext.CancellationToken);

        Assert.AreEqual(7, result.Players.Online);
        Assert.IsTrue(result.Latency >= 0);
    }

    [TestMethod]
    public async Task ClosedBeforeStatusIsReportedAsConnectionClosed()
    {
        await using var server = new FakeTcpServer(async (stream, ct) =>
        {
            await _ReadFrameAsync(stream, ct);
            await _ReadFrameAsync(stream, ct);
        });

        using var service = new McPingService("localhost", new IPEndPoint(IPAddress.Loopback, server.Port), 5000);
        var ex = await Assert.ThrowsExactlyAsync<McPingException>(
            async () => await service.QueryAsync(TestContext.CancellationToken));
        Assert.AreEqual(McPingFailureKind.ConnectionClosed, ex.Kind);
        Assert.IsTrue(ex.IsProtocolFailure);
    }

    [TestMethod]
    public async Task RefusedConnectionIsReportedAsUnreachable()
    {
        var port = _GetUnusedTcpPort();
        using var service = new McPingService("localhost", new IPEndPoint(IPAddress.Loopback, port), 5000);
        var ex = await Assert.ThrowsExactlyAsync<McPingException>(
            async () => await service.QueryAsync(TestContext.CancellationToken));
        Assert.AreEqual(McPingFailureKind.Unreachable, ex.Kind);
        Assert.IsFalse(ex.IsProtocolFailure);
    }

    [TestMethod]
    public async Task PingAsyncReturnsNullInsteadOfThrowing()
    {
        var port = _GetUnusedTcpPort();
        using var service = McPingServiceFactory.CreateService(new IPEndPoint(IPAddress.Loopback, port), 5000);
        Assert.IsNull(await service.PingAsync(TestContext.CancellationToken));
    }

    // ===== 回退链 =====

    [TestMethod]
    public async Task QueryFallsBackToLegacyProtocol()
    {
        await using var server = new FakeTcpServer(_HandleLegacyOnlyAsync);

        var result = await McServerQuery.QueryAsync($"127.0.0.1:{server.Port}", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(McServerEdition.JavaLegacy, result.Edition);
        Assert.AreEqual("1.6.4", result.Version.Name);
        Assert.AreEqual(78, result.Version.Protocol);
        Assert.AreEqual(3, result.Players.Online);
        Assert.AreEqual(20, result.Players.Max);
        Assert.AreEqual("§aA Legacy Server", result.Description);
    }

    [TestMethod]
    public async Task QueryFallsBackToBedrockWhenJavaIsUnreachable()
    {
        await using var server = new FakeBedrockServer(
            "MCPE;§aBedrock §gServer;766;1.21.50;5;10;12345678;Level §mName;Survival;1;19132;19133;");

        var result = await McServerQuery.QueryAsync($"127.0.0.1:{server.Port}", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(McServerEdition.Bedrock, result.Edition);
        Assert.AreEqual("1.21.50", result.Version.Name);
        Assert.AreEqual(766, result.Version.Protocol);
        Assert.AreEqual(5, result.Players.Online);
        Assert.AreEqual(10, result.Players.Max);
        Assert.AreEqual("§aBedrock #DDD605Server\nLevel #971607Name", result.Description);
    }

    [TestMethod]
    public async Task QueryPrefersJavaOverBedrock()
    {
        await using var java = new FakeTcpServer(_HandleVelocityLikeAsync);
        await using var bedrock = new FakeBedrockServer("MCPE;Bedrock;766;1.21.50;1;2;1;Level;Survival;", java.Port);

        var result = await McServerQuery.QueryAsync($"127.0.0.1:{java.Port}", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(McServerEdition.Java, result.Edition);
        Assert.AreEqual("Velocity 3.4.0", result.Version.Name);
    }

    [TestMethod]
    public async Task QueryReportsJavaReasonWhenEverythingFails()
    {
        var port = _GetUnusedTcpPort();
        var ex = await Assert.ThrowsExactlyAsync<McPingException>(async () =>
            await McServerQuery.QueryAsync($"127.0.0.1:{port}", cancellationToken: TestContext.CancellationToken));
        Assert.AreEqual(McPingFailureKind.Unreachable, ex.Kind);
    }

    // ===== 状态 JSON 与 MOTD 解析 =====

    [TestMethod]
    public void ParserKeepsLegacyStringMotd()
    {
        var result = McPingResponseParser.Parse("""{"description":"§aHello §lWorld","players":{"max":20,"online":0}}""");
        Assert.AreEqual("§aHello §lWorld", result.Description);
    }

    [TestMethod]
    public void ParserAppliesStyleInheritance()
    {
        var node = JsonNode.Parse(
            """{"text":"","extra":[{"text":"A","color":"red","extra":[{"text":"B","bold":true}]},{"text":"C"}]}""");
        Assert.AreEqual("§cA§c§lB§rC", McPingResponseParser.MotdToLegacyText(node));
    }

    [TestMethod]
    public void ParserTreatsArrayTailAsSiblingsOfFirstComponent()
    {
        Assert.AreEqual("§6AB",
            McPingResponseParser.MotdToLegacyText(JsonNode.Parse("""[{"text":"A","color":"gold"},"B"]""")));
        Assert.AreEqual("#FF8800§lX",
            McPingResponseParser.MotdToLegacyText(JsonNode.Parse("""["",{"text":"X","color":"#ff8800","bold":true}]""")));
    }

    [TestMethod]
    public void ParserHandlesTranslateAndBungeeHex()
    {
        Assert.AreEqual("Bob has 3", McPingResponseParser.MotdToLegacyText(JsonNode.Parse(
            """{"translate":"x.y","fallback":"%s has %s","with":["Bob",{"text":"3","color":"red"}]}""")));
        Assert.AreEqual("#FF0000Red", McPingResponseParser.MotdToLegacyText(JsonValue.Create("§x§f§f§0§0§0§0Red")));
    }

    [TestMethod]
    public void ParserToleratesLooseFields()
    {
        var result = McPingResponseParser.Parse(
            """{"description":{"text":"Hi"},"players":{"max":"20","online":3.0},"version":"Paper"}""");
        Assert.AreEqual("Hi", result.Description);
        Assert.AreEqual(20, result.Players.Max);
        Assert.AreEqual(3, result.Players.Online);
        Assert.AreEqual("Paper", result.Version.Name);
        Assert.AreEqual(-1, result.Version.Protocol);
        Assert.IsNull(result.Favicon);

        var minimal = McPingResponseParser.Parse("""{"description":"Only MOTD"}""");
        Assert.AreEqual(0, minimal.Players.Max);
        Assert.AreEqual(string.Empty, minimal.Version.Name);
    }

    [TestMethod]
    public void ParserReadsForgeData()
    {
        var result = McPingResponseParser.Parse(
            """{"description":"","forgeData":{"fmlNetworkVersion":3,"mods":[{"modId":"forge","modmarker":"47.2.0"}]}}""");
        Assert.AreEqual("FML3", result.ModInfo!.Type);
        Assert.AreEqual("forge", result.ModInfo.ModList.Single().Id);
    }

    [TestMethod]
    public void ParserRejectsNonStatusJson()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => McPingResponseParser.Parse("<html></html>"));
        Assert.ThrowsExactly<InvalidDataException>(() => McPingResponseParser.Parse("""{"error":"nope"}"""));
    }

    [TestMethod]
    public void LegacyParserReadsBothFormats()
    {
        var modern = LegacyMcPingService.ParseResponse("§1\078\01.6.4\0Hello\05\020");
        Assert.AreEqual("1.6.4", modern.Version.Name);
        Assert.AreEqual(5, modern.Players.Online);
        Assert.AreEqual(20, modern.Players.Max);

        var beta = LegacyMcPingService.ParseResponse("Old Server§2§10");
        Assert.AreEqual("Old Server", beta.Description);
        Assert.AreEqual(2, beta.Players.Online);
        Assert.AreEqual(10, beta.Players.Max);
    }

    [TestMethod]
    public void BedrockPongRoundTrip()
    {
        var ping = BedrockPingService.BuildPingPacket(1234);
        Assert.AreEqual(33, ping.Length);
        var pong = FakeBedrockServer.BuildPong(ping, "MCPE;Hi;1;1.0;0;1;");
        Assert.IsTrue(BedrockPingService.TryParsePong(pong, out var time, out var advertisement));
        Assert.AreEqual(1234L, time);
        Assert.AreEqual("MCPE;Hi;1;1.0;0;1;", advertisement);
    }

    // ===== 假服务器 =====

    private sealed record HandshakeInfo(int ProtocolVersion, string Host, int Port, int NextState);

    /// <summary>
    /// 模拟 Velocity：状态响应异步生成（延迟发送）；在状态发出之前收到 Ping 就回 Pong 并断开。
    /// </summary>
    private static async Task _HandleVelocityLikeAsync(
        NetworkStream stream,
        CancellationToken ct,
        Action<HandshakeInfo>? onHandshake)
    {
        // 读帧不能放进 onHandshake?.Invoke(...) 的参数里：回调为 null 时整个参数表达式都不会求值
        var handshakeFrame = await _ReadFrameAsync(stream, ct);
        onHandshake?.Invoke(_ParseHandshake(handshakeFrame));
        var statusRequest = await _ReadFrameAsync(stream, ct);
        Assert.AreEqual(0x00, statusRequest[0]);

        var nextFrame = _ReadFrameAsync(stream, ct);
        if (await Task.WhenAny(nextFrame, Task.Delay(200, ct)) == nextFrame)
        {
            await stream.WriteAsync(_Frame(0x01, (await nextFrame)[1..]), ct);
            return;
        }

        await stream.WriteAsync(_StatusResponse(StatusJson), ct);
        var ping = await nextFrame;
        await stream.WriteAsync(_Frame(0x01, ping[1..]), ct);
    }

    private static Task _HandleVelocityLikeAsync(NetworkStream stream, CancellationToken ct) =>
        _HandleVelocityLikeAsync(stream, ct, null);

    /// <summary>
    /// 模拟 1.6 服务器：只认 0xFE 开头的旧版请求，其余一律断开。
    /// </summary>
    private static async Task _HandleLegacyOnlyAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[3];
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), ct);
        if (header[0] != 0xFE) return;
        await stream.ReadExactlyAsync(header.AsMemory(1, 2), ct);

        var channelLength = new byte[2];
        await stream.ReadExactlyAsync(channelLength, ct);
        await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16BigEndian(channelLength) * 2], ct);
        var dataLength = new byte[2];
        await stream.ReadExactlyAsync(dataLength, ct);
        await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16BigEndian(dataLength)], ct);

        const string response = "§1\078\01.6.4\0§aA Legacy Server\03\020";
        var body = Encoding.BigEndianUnicode.GetBytes(response);
        var packet = new byte[3 + body.Length];
        packet[0] = 0xFF;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), (ushort)response.Length);
        body.CopyTo(packet, 3);
        await stream.WriteAsync(packet, ct);
    }

    private static HandshakeInfo _ParseHandshake(byte[] frame)
    {
        var offset = 0;
        Assert.AreEqual(0x00, _ReadVarInt(frame, ref offset));
        var protocol = _ReadVarInt(frame, ref offset);
        var hostLength = _ReadVarInt(frame, ref offset);
        var host = Encoding.UTF8.GetString(frame, offset, hostLength);
        offset += hostLength;
        var port = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset));
        offset += 2;
        var nextState = _ReadVarInt(frame, ref offset);
        return new HandshakeInfo(protocol, host, port, nextState);
    }

    private static byte[] _BuildHandshake(int protocol, string host, int port)
    {
        var hostBytes = Encoding.UTF8.GetBytes(host);
        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        return _Frame(0x00, [.. _VarInt(protocol), .. _VarInt(hostBytes.Length), .. hostBytes, .. portBytes, .. _VarInt(1)]);
    }

    private static byte[] _StatusResponse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return _Frame(0x00, [.. _VarInt(bytes.Length), .. bytes]);
    }

    private static byte[] _Frame(int packetId, byte[] payload)
    {
        byte[] body = [.. _VarInt(packetId), .. payload];
        return [.. _VarInt(body.Length), .. body];
    }

    private static byte[] _VarInt(int value)
    {
        var bytes = new List<byte>();
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            bytes.Add(b);
        } while (v != 0);

        return [.. bytes];
    }

    private static int _ReadVarInt(byte[] data, ref int offset)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = data[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }

        throw new InvalidDataException();
    }

    private static async Task<byte[]> _ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var length = 0;
        var one = new byte[1];
        for (var shift = 0; ; shift += 7)
        {
            await stream.ReadExactlyAsync(one, ct);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
        }

        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, ct);
        return frame;
    }

    private static int _GetUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeTcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<NetworkStream, CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public FakeTcpServer(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _handler = handler;
            _listener.Start();
            _acceptLoop = Task.Run(_AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task _AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await _handler(client.GetStream(), _cts.Token);
                            // 先 Shutdown 再关闭，否则 Windows 会对用过异步 IO 的套接字发 RST 而不是 FIN
                            client.Client.Shutdown(SocketShutdown.Both);
                        }
                        catch (Exception)
                        {
                            // 地址解析器的连通性探测会连上就断开，这里忽略
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            _cts.Dispose();
        }
    }

    private sealed class FakeBedrockServer : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly string _advertisement;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeBedrockServer(string advertisement, int port = 0)
        {
            _advertisement = advertisement;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _loop = Task.Run(_LoopAsync);
        }

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public static byte[] BuildPong(byte[] ping, string advertisement)
        {
            var text = Encoding.UTF8.GetBytes(advertisement);
            var pong = new byte[35 + text.Length];
            pong[0] = 0x1C;
            ping.AsSpan(1, 8).CopyTo(pong.AsSpan(1)); // 回显时间
            BinaryPrimitives.WriteInt64BigEndian(pong.AsSpan(9), 0x0102030405060708); // 服务器 GUID
            ping.AsSpan(9, 16).CopyTo(pong.AsSpan(17)); // 离线消息标识
            BinaryPrimitives.WriteUInt16BigEndian(pong.AsSpan(33), (ushort)text.Length);
            text.CopyTo(pong, 35);
            return pong;
        }

        private async Task _LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                if (received.Buffer.Length < 33 || received.Buffer[0] != 0x01) continue;
                var pong = BuildPong(received.Buffer, _advertisement);
                try
                {
                    await _udp.SendAsync(pong, received.RemoteEndPoint, _cts.Token);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _udp.Dispose();
            await _loop;
            _cts.Dispose();
        }
    }
}
