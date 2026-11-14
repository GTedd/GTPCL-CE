using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

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
    ///     查询提交：把短提交号或标签解析为 40 位提交号，并给出提交者时间（ISO 8601，UTC）。<br/>
    ///     先走 GitHub API 与 Horizon 镜像（镜像只代理 horizon-rust/horizon）；未登录的 GitHub API 每个 IP 每小时只有
    ///     60 次，国内常与他人共用出口 IP 而很快用完，所以再退到网页端的提交 Atom 订阅（不占 API 额度，也能走加速站）。
    /// </summary>
    public static async Task<(string Sha, string Date)> GetCommitAsync(
        HttpClient http, string repository, string reference, CancellationToken token = default)
    {
        var failures = new List<string>();
        Exception? last = null;
        foreach (var apiBase in ApiBases)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/repos/{repository}/commits/{reference}");
                request.Headers.UserAgent.ParseAdd("GTPCL-CE");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await http.SendAsync(request, timeout.Token);
                if (_IsRateLimited(response))
                    throw new HttpRequestException("未登录的 GitHub API 已达到每小时请求上限", null, response.StatusCode);
                response.EnsureSuccessStatusCode();
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))?.AsObject()
                           ?? throw new InvalidDataException("GitHub 返回的不是 JSON");
                var sha = json["sha"]?.GetValue<string>();
                if (sha is null || !_IsFullSha(sha)) throw new InvalidDataException("GitHub 返回的提交号无效");
                return (sha.ToLowerInvariant(), _NormalizeDate(json["commit"]?["committer"]?["date"]?.GetValue<string>()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                last = ex;
                failures.Add($"{new Uri(apiBase).Host}: {ex.Message}");
            }
        }

        var feed = $"https://github.com/{repository}/commits/{Uri.EscapeDataString(reference)}.atom";
        foreach (var url in BuiltInGithubProxies.Select(prefix => prefix + feed).Prepend(feed))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("GTPCL-CE");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                return ParseCommitFeed(await response.Content.ReadAsStringAsync(timeout.Token), reference);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                last = ex;
                failures.Add($"{new Uri(url).Host} (Atom): {ex.Message}");
            }
        }

        throw new InvalidOperationException($"无法查询 {repository} 的提交 {reference}：{string.Join("；", failures)}", last);
    }

    /// <summary>
    ///     解析 <c>https://github.com/&lt;仓库&gt;/commits/&lt;引用&gt;.atom</c>：第一个条目就是该引用指向的提交，
    ///     条目 id 末尾是 40 位提交号，<c>updated</c> 是提交者时间。
    /// </summary>
    public static (string Sha, string Date) ParseCommitFeed(string xml, string reference)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var entry = XDocument.Parse(xml).Root?.Element(atom + "entry")
                    ?? throw new InvalidDataException("提交订阅里没有提交");
        var id = entry.Element(atom + "id")?.Value.Trim() ?? "";
        var sha = id[(id.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (!_IsFullSha(sha)) throw new InvalidDataException("提交订阅里的提交号无效");
        // 引用本身是提交号时，订阅给出的提交必须以它开头（防止拿到别的分支的提交）
        if (reference.Length is >= 7 and <= 40 && reference.All(Uri.IsHexDigit)
            && !sha.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"提交订阅给出的是 {sha[..7]}，不是 {reference}");
        return (sha, _NormalizeDate(entry.Element(atom + "updated")?.Value));
    }

    private static bool _IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests
        && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
        && remaining.FirstOrDefault() == "0";

    private static bool _IsFullSha(string sha) => sha.Length == 40 && sha.All(Uri.IsHexDigit);

    /// <summary>统一成 <c>yyyy-MM-ddTHH:mm:ssZ</c>，不同来源的时间才能直接按字符串比较先后。</summary>
    private static string _NormalizeDate(string? date) =>
        DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : "";

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
