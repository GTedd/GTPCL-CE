using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Logging;
using PCL.Core.Minecraft;

namespace PCL.Core.Link.McPing;

/// <summary>
/// 服务器状态查询的参数。
/// </summary>
public sealed record McServerQueryOptions
{
    public static McServerQueryOptions Default { get; } = new();

    /// <summary>首次 Java 版查询的超时（毫秒）。</summary>
    public int JavaTimeout { get; init; } = 10000;

    /// <summary>Java 版换协议重试时每次的超时（毫秒）。</summary>
    public int FallbackTimeout { get; init; } = 5000;

    /// <summary>基岩版查询的超时（毫秒）。</summary>
    public int BedrockTimeout { get; init; } = 3000;

    /// <summary>现代协议失败时是否尝试 1.6 及以下的旧版协议。</summary>
    public bool EnableLegacyFallback { get; init; } = true;

    /// <summary>Java 版失败时是否采用基岩版的查询结果。</summary>
    public bool EnableBedrockFallback { get; init; } = true;
}

/// <summary>
/// 按玩家填写的地址查询服务器状态：解析 SRV 与 DNS 后依次尝试 Java 版各协议，并行探测基岩版作为兜底。
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Java 版现代协议，握手协议号 -1，握手地址使用玩家填写的原始域名。</item>
/// <item>连接成功但协议交互失败时，换用具体的协议号重试一次，再尝试 1.6 及以下的旧版协议。
/// 连接失败或超时则不再重试——换协议也连不上。</item>
/// <item>基岩版走 UDP，与 Java 版同时开始，但只在 Java 版全部失败时才采用，避免同时开着两个版本的服务器被认成基岩版。</item>
/// </list>
/// </remarks>
public static class McServerQuery
{
    private const string ModuleName = "McServerQuery";

    /// <summary>
    /// 查询服务器状态。
    /// </summary>
    /// <param name="address">玩家填写的服务器地址，可以带端口。</param>
    /// <param name="options">查询参数，默认 <see cref="McServerQueryOptions.Default" />。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="McPingException">所有方式都失败；<see cref="Exception.Message" /> 为 Java 版查询的失败原因。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> 被取消。</exception>
    public static async Task<McPingResult> QueryAsync(
        string address,
        McServerQueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= McServerQueryOptions.Default;

        string host;
        int? explicitPort;
        try
        {
            (host, explicitPort) = ServerAddressResolver.ParseAddress(address);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new McPingException(McPingFailureKind.Unreachable, ex.Message, ex);
        }

        CancellationTokenSource? bedrockCts = null;
        Task<McPingResult>? bedrockTask = null;
        if (options.EnableBedrockFallback)
        {
            bedrockCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bedrockTask = _QueryBedrockAsync(host, explicitPort ?? BedrockPingService.DefaultPort,
                options.BedrockTimeout, bedrockCts.Token);
        }

        try
        {
            return await _QueryJavaAsync(address, options, cancellationToken).ConfigureAwait(false);
        }
        catch (McPingException javaError) when (bedrockTask is not null)
        {
            try
            {
                var result = await bedrockTask.ConfigureAwait(false);
                LogWrapper.Info(ModuleName, $"{address} Java 版查询失败（{javaError.Message}），基岩版查询成功");
                return result;
            }
            catch (McPingException bedrockError)
            {
                LogWrapper.Debug(ModuleName, $"{address} 基岩版查询失败：{bedrockError.Message}");
                ExceptionDispatchInfo.Throw(javaError);
                throw;
            }
        }
        finally
        {
            if (bedrockTask is not null)
            {
                // Java 版成功或调用方取消时不再等待基岩版；收尾时观察掉它的异常并释放取消源
                bedrockCts!.Cancel();
                _ = bedrockTask.ContinueWith(static (task, state) =>
                    {
                        _ = task.Exception;
                        ((CancellationTokenSource)state!).Dispose();
                    }, bedrockCts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private static async Task<McPingResult> _QueryJavaAsync(
        string address,
        McServerQueryOptions options,
        CancellationToken cancellationToken)
    {
        ServerAddressResolver.ResolvedServerAddress resolved;
        try
        {
            resolved = await ServerAddressResolver.GetResolvedServerAddressAsync(address, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new McPingException(McPingFailureKind.Unreachable, ex.Message, ex);
        }

        var endpoint = new IPEndPoint(
            await _ResolveIpAsync(resolved.Ip ?? resolved.Host, cancellationToken).ConfigureAwait(false),
            resolved.Port);

        McPingException? firstError = null;
        var attemptIndex = 0;
        foreach (var (name, service) in _CreateJavaAttempts(resolved.Host, endpoint, options))
        {
            using (service)
            {
                try
                {
                    var result = await service.QueryAsync(cancellationToken).ConfigureAwait(false);
                    if (attemptIndex > 0)
                        LogWrapper.Info(ModuleName, $"{address} 改用{name}查询成功");
                    return result;
                }
                catch (McPingException ex)
                {
                    LogWrapper.Info(ModuleName, $"{address}（{endpoint}）{name}查询失败：{ex.Message}");
                    firstError ??= ex;
                    if (!ex.IsProtocolFailure) break;
                }
            }

            attemptIndex++;
        }

        throw firstError!;
    }

    private static IEnumerable<(string Name, IMcPingService Service)> _CreateJavaAttempts(
        string host,
        IPEndPoint endpoint,
        McServerQueryOptions options)
    {
        yield return ("现代协议", new McPingService(host, endpoint, options.JavaTimeout));
        yield return ($"现代协议（协议号 {McPingService.FallbackProtocolVersion}）",
            new McPingService(host, endpoint, options.FallbackTimeout)
            {
                ProtocolVersion = McPingService.FallbackProtocolVersion
            });
        if (options.EnableLegacyFallback)
            yield return ("旧版协议", new LegacyMcPingService(host, endpoint, options.FallbackTimeout));
    }

    private static async Task<McPingResult> _QueryBedrockAsync(
        string host,
        int port,
        int timeout,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host.TrimEnd('.'), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new McPingException(McPingFailureKind.Unreachable, ex.Message, ex);
            }
        }

        // 基岩版服务器大多只监听 IPv4，IPv4 与 IPv6 各取一个同时探测，先回应者胜出
        var candidates = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(1)
            .Concat(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(1))
            .ToList();
        if (candidates.Count == 0)
            throw new McPingException(McPingFailureKind.Unreachable, $"No address for {host}");

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = candidates
            .Select(ip => new BedrockPingService(host, new IPEndPoint(ip, port), timeout).QueryAsync(raceCts.Token))
            .ToList();

        McPingException? firstError = null;
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                try
                {
                    return await done.ConfigureAwait(false);
                }
                catch (McPingException ex)
                {
                    firstError ??= ex;
                }
            }
        }
        finally
        {
            await raceCts.CancelAsync().ConfigureAwait(false);
            foreach (var task in pending)
                _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        throw firstError!;
    }

    private static async Task<IPAddress> _ResolveIpAsync(string hostOrIp, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(hostOrIp, out var ip)) return ip;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostOrIp.TrimEnd('.'), cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault()
                   ?? throw new McPingException(McPingFailureKind.Unreachable, $"No address for {hostOrIp}");
        }
        catch (SocketException ex)
        {
            throw new McPingException(McPingFailureKind.Unreachable, ex.Message, ex);
        }
    }
}
