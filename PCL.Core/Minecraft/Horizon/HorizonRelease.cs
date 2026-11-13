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
    IReadOnlyList<string> SignatureUrls)
{
    /// <summary>构建对应的提交（版本号的最后一段）。</summary>
    public string Commit => Version[(Version.LastIndexOf('-') + 1)..];
}

/// <summary>
///     查询 horizon-rust/horizon 的最新 Nightly。API 先走 GitHub，失败后走 Horizon 官方的只读镜像；
///     下载地址按内置的 GitHub 加速站在前、直连在后排列（下载后仍做签名校验，加速站无法篡改）。
/// </summary>
public static class HorizonRelease
{
    public const string Repository = "horizon-rust/horizon";
    public const string NightlyTag = "nightly";

    /// <summary>
    ///     内置的 GitHub 下载加速前缀，按实测速度排序；全部失败时回落到直连。
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInGithubProxies =
    [
        "https://gh-proxy.org/",
        "https://gh-proxy.com/",
        "https://ghfast.top/"
    ];

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
    ///     查询最新 Nightly，下载地址使用内置的加速前缀。
    /// </summary>
    public static async Task<HorizonRemoteBuild> GetLatestNightlyAsync(HttpClient http, CancellationToken token = default)
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
                return Parse(json, BuiltInGithubProxies);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException($"无法获取 Horizon 的最新版本：{last?.Message}", last);
    }

    /// <summary>
    ///     读取构建所在提交的 research-lock.json，得到它固定的 Minecraft 版本（如 26.3）。
    /// </summary>
    public static async Task<string> GetMinecraftVersionAsync(HttpClient http, HorizonRemoteBuild build, CancellationToken token = default)
    {
        var direct = $"https://raw.githubusercontent.com/{Repository}/{build.Commit}/research-lock.json";
        Exception? last = null;
        foreach (var url in BuiltInGithubProxies.Select(prefix => prefix + direct).Append(direct))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var text = await http.GetStringAsync(url, timeout.Token);
                return JsonNode.Parse(text)?["minecraft"]?["version"]?.GetValue<string>()
                       ?? throw new InvalidDataException("research-lock.json 缺少 minecraft.version");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException($"无法获取 Horizon {build.Version} 对应的 Minecraft 版本：{last?.Message}", last);
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
