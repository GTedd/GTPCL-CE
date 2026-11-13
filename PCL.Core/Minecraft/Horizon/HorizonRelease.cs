using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     GitHub 上的一个 Horizon 构建。<see cref="Version" /> 是它签名所用的版本（nightly-日期-提交）。
/// </summary>
public sealed record HorizonRemoteBuild(
    string Version,
    string Title,
    string AssetName,
    long AssetSize,
    IReadOnlyList<string> AssetUrls,
    IReadOnlyList<string> SumsUrls,
    IReadOnlyList<string> SignatureUrls);

/// <summary>
///     查询 horizon-rust/horizon 的最新 Nightly。API 先走 GitHub，失败后走 Horizon 官方的只读镜像；
///     下载地址在 GitHub 直连之外，可以加上用户配置的 GitHub 加速前缀（下载后仍做签名校验，镜像无法篡改）。
/// </summary>
public static class HorizonRelease
{
    public const string Repository = "horizon-rust/horizon";
    public const string NightlyTag = "nightly";

    private static readonly string[] ApiBases =
    [
        "https://api.github.com",
        "https://mcp.playhorizon.dev/github/public"
    ];

    /// <summary>
    ///     本机对应的构建压缩包名。
    /// </summary>
    public static string AssetName =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "horizon-windows-arm64.zip" : "horizon-windows-x64.zip";

    /// <summary>
    ///     查询最新 Nightly。<paramref name="githubProxyPrefixes" /> 形如 <c>https://example.com/</c>，
    ///     会拼在 github.com 下载地址前面，按顺序排在直连之前。
    /// </summary>
    public static async Task<HorizonRemoteBuild> GetLatestNightlyAsync(
        HttpClient http,
        IReadOnlyList<string> githubProxyPrefixes,
        CancellationToken token = default)
    {
        Exception? last = null;
        foreach (var apiBase in ApiBases)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/repos/{Repository}/releases/tags/{NightlyTag}");
                request.Headers.UserAgent.ParseAdd("GTPCL-CE");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await http.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))?.AsObject()
                           ?? throw new InvalidDataException("GitHub 返回的不是 JSON");
                return Parse(json, githubProxyPrefixes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException($"无法获取 Horizon 的最新版本：{last?.Message}", last);
    }

    /// <summary>
    ///     解析 GitHub 的 release JSON。
    /// </summary>
    public static HorizonRemoteBuild Parse(JsonObject release, IReadOnlyList<string> githubProxyPrefixes)
    {
        var title = release["name"]?.GetValue<string>() ?? "";
        var version = HorizonReleaseVerifier.NightlyVersionFromTitle(title)
                      ?? throw new InvalidDataException($"无法从发布标题“{title}”得到 Nightly 版本，已停止安装。");
        var assets = (release["assets"]?.AsArray() ?? []).OfType<JsonObject>().ToList();

        JsonObject Asset(string name) =>
            assets.FirstOrDefault(asset => asset["name"]?.GetValue<string>() == name)
            ?? throw new InvalidDataException($"{title} 中没有 {name}");

        IReadOnlyList<string> Urls(JsonObject asset)
        {
            var direct = asset["browser_download_url"]?.GetValue<string>()
                         ?? throw new InvalidDataException("发布文件缺少下载地址");
            return githubProxyPrefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(prefix => prefix.Trim().TrimEnd('/') + "/" + direct)
                .Append(direct)
                .ToList();
        }

        var archive = Asset(AssetName);
        return new HorizonRemoteBuild(
            version,
            title,
            AssetName,
            archive["size"]?.GetValue<long>() ?? 0,
            Urls(archive),
            Urls(Asset(HorizonReleaseVerifier.SumsFileName)),
            Urls(Asset(HorizonReleaseVerifier.NightlySignatureFileName)));
    }
}
