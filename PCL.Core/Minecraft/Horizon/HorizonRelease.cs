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
///     GitHub 上的一个 Horizon 构建。<see cref="Version" /> 是它签名所用的版本：
///     Nightly 为 <c>nightly-日期-提交</c>，正式版为标签本身（如 <c>v0.2.97</c>）。
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
    /// <summary>GitHub 上的标签：滚动的 <c>nightly</c>、归档的 <c>nightly-日期</c> 或 <c>v0.2.x</c>。</summary>
    public string Tag { get; init; } = HorizonRelease.NightlyTag;

    /// <summary>签名文件名：Nightly 为 <c>SHA256SUMS.v2.sig</c>，正式版为 <c>SHA256SUMS.sig</c>。</summary>
    public string SignatureFileName { get; init; } = HorizonReleaseVerifier.NightlySignatureFileName;

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>发布说明（Markdown 原文）。</summary>
    public string Changelog { get; init; } = "";

    public bool IsNightly => Version.StartsWith("nightly-", StringComparison.Ordinal);

    /// <summary>可当作 Git 引用的提交或标签：Nightly 为短提交号，正式版为标签。</summary>
    public string Commit => IsNightly ? Version[(Version.LastIndexOf('-') + 1)..] : Version;

    /// <summary>列表里显示的名称：Nightly 用发布标题，正式版为“Horizon 0.2.97”。</summary>
    public string DisplayName => IsNightly ? Title : "Horizon " + Version.TrimStart('v');

    /// <summary>正式版标题冒号后的摘要，Nightly 为空。</summary>
    public string Summary
    {
        get
        {
            if (IsNightly) return "";
            var colon = Title.IndexOf(": ", StringComparison.Ordinal);
            return colon < 0 ? "" : Title[(colon + 2)..];
        }
    }

    public string ReleaseUrl => $"https://github.com/{HorizonRelease.Repository}/releases/tag/{Uri.EscapeDataString(Tag)}";
}

/// <summary>
///     查询 horizon-rust/horizon 的游戏版本发布。API 先走 GitHub，失败后走 Horizon 官方的只读镜像；
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

    private const string X64AssetName = "horizon-windows-x64.zip";
    private const int PageSize = 100;
    private const int MaxPages = 6;

    /// <summary>
    ///     是否为游戏版本的标签：滚动的 <c>nightly</c>、归档的 <c>nightly-*</c> 与 <c>v</c> 加数字
    ///     （<c>launcher@</c> 等启动器自身的发布不算），与官方启动器的规则一致。
    /// </summary>
    public static bool IsGameTag([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? tag) =>
        tag is not null
        && (tag == NightlyTag
            || tag.StartsWith("nightly-", StringComparison.Ordinal)
            || (tag.Length > 1 && tag[0] == 'v' && char.IsAsciiDigit(tag[1])));

    /// <summary>
    ///     取 horizon-rust/horizon 的全部游戏版本发布（跳过草稿与启动器发布），返回精简后的发布 JSON，便于缓存；
    ///     用 <see cref="ParseBuilds" /> 解析。官方启动器只看第一页，这里按页取到“没有本平台构建的旧版本”为止，
    ///     所以第二页上仍可安装的 v0.2.47–v0.2.56 也在列表中。GitHub API 限流或连不上时走 Horizon 镜像（支持分页）。
    /// </summary>
    public static async Task<JsonArray> FetchGameReleasesAsync(HttpClient http, CancellationToken token = default)
    {
        var failures = new List<string>();
        Exception? last = null;
        foreach (var apiBase in ApiBases)
        {
            try
            {
                var result = new JsonArray();
                for (var page = 1; page <= MaxPages; page++)
                {
                    var releases = await _GetJsonArrayAsync(http,
                        $"{apiBase}/repos/{Repository}/releases?per_page={PageSize}&page={page}", token);
                    var games = releases.OfType<JsonObject>()
                        .Where(release => release["draft"]?.GetValue<bool>() != true
                                          && IsGameTag(release["tag_name"]?.GetValue<string>()))
                        .ToList();
                    foreach (var release in games) result.Add(_Slim(release));
                    if (releases.Count < PageSize) break;
                    // 发布按时间从新到旧：本页最旧的游戏版本已没有本平台构建时，更早的也不会有
                    if (games.Count > 0 && _PickArchive(games[^1]) is null) break;
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                last = ex;
                failures.Add($"{new Uri(apiBase).Host}: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"无法获取 Horizon 的版本列表：{string.Join("；", failures)}", last);
    }

    private static async Task<JsonArray> _GetJsonArrayAsync(HttpClient http, string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("GTPCL-CE");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await http.SendAsync(request, timeout.Token);
        if (_IsRateLimited(response))
            throw new HttpRequestException("未登录的 GitHub API 已达到每小时请求上限", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)) as JsonArray
               ?? throw new InvalidDataException("GitHub 返回的不是发布列表");
    }

    /// <summary>只留解析与显示需要的字段（发布 JSON 里的作者、反应等信息很大）。</summary>
    private static JsonObject _Slim(JsonObject release)
    {
        var assets = new JsonArray();
        foreach (var asset in (release["assets"] as JsonArray ?? []).OfType<JsonObject>())
            assets.Add(new JsonObject
            {
                ["name"] = asset["name"]?.DeepClone(),
                ["size"] = asset["size"]?.DeepClone(),
                ["browser_download_url"] = asset["browser_download_url"]?.DeepClone()
            });
        return new JsonObject
        {
            ["tag_name"] = release["tag_name"]?.DeepClone(),
            ["name"] = release["name"]?.DeepClone(),
            ["prerelease"] = release["prerelease"]?.DeepClone(),
            ["published_at"] = release["published_at"]?.DeepClone(),
            ["body"] = release["body"]?.DeepClone(),
            ["assets"] = assets
        };
    }

    private static JsonObject? _FindAsset(JsonObject release, string name) =>
        (release["assets"] as JsonArray ?? []).OfType<JsonObject>()
        .FirstOrDefault(asset => asset["name"]?.GetValue<string>() == name);

    /// <summary>本平台的构建压缩包；ARM64 上没有 arm64 包时与官方启动器一样退回 x64 包。</summary>
    private static JsonObject? _PickArchive(JsonObject release) =>
        _FindAsset(release, AssetName) ?? (AssetName == X64AssetName ? null : _FindAsset(release, X64AssetName));

    /// <summary>
    ///     解析发布列表：只留本平台有构建、带签名清单的游戏版本；滚动 nightly 与当天的归档是同一构建时只留滚动的；
    ///     按从新到旧排序（Nightly 按日期，正式版按版本号，Nightly 在前），第一个即最新版本。
    /// </summary>
    public static List<HorizonRemoteBuild> ParseBuilds(JsonArray releases, IReadOnlyList<string> githubProxyPrefixes)
    {
        var builds = new List<HorizonRemoteBuild>();
        foreach (var release in releases.OfType<JsonObject>())
        {
            var build = TryParse(release, githubProxyPrefixes);
            if (build is null) continue;
            var same = builds.FindIndex(b => b.Version == build.Version);
            if (same < 0) builds.Add(build);
            else if (build.Tag == NightlyTag) builds[same] = build;
        }

        builds.Sort(CompareNewestFirst);
        return builds;
    }

    /// <summary>从新到旧比较两个构建。</summary>
    public static int CompareNewestFirst(HorizonRemoteBuild a, HorizonRemoteBuild b)
    {
        if (a.IsNightly != b.IsNightly) return a.IsNightly ? -1 : 1;
        if (a.IsNightly)
        {
            var byDate = string.CompareOrdinal(_NightlyDate(b.Version), _NightlyDate(a.Version));
            if (byDate != 0) return byDate;
            var aRolling = a.Tag == NightlyTag;
            if (aRolling != (b.Tag == NightlyTag)) return aRolling ? -1 : 1;
            return Nullable.Compare(b.PublishedAt, a.PublishedAt);
        }

        var byNumber = _ReleaseOrder(b.Version).CompareTo(_ReleaseOrder(a.Version));
        return byNumber != 0 ? byNumber : Nullable.Compare(b.PublishedAt, a.PublishedAt);
    }

    private static string _NightlyDate(string version) => version.Length >= 18 ? version[8..18] : "";

    /// <summary>正式版的先后：0.2.x 在签名前的日期版（vYYYY.MM.DD）之后。</summary>
    private static (int, Version) _ReleaseOrder(string tag)
    {
        var number = System.Version.TryParse(tag.TrimStart('v'), out var parsed) ? parsed : new Version(0, 0);
        return (number.Major >= 2000 ? 0 : 1, number);
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
    ///     解析 GitHub 的 release JSON；不是可安装的游戏版本时抛出异常。
    /// </summary>
    public static HorizonRemoteBuild Parse(JsonObject release, IReadOnlyList<string> githubProxyPrefixes) =>
        TryParse(release, githubProxyPrefixes)
        ?? throw new InvalidDataException(
            $"{release["name"]?.GetValue<string>() ?? release["tag_name"]?.GetValue<string>()} 不是本平台可安装的 Horizon 版本。");

    /// <summary>
    ///     解析 GitHub 的 release JSON。不是游戏版本、没有本平台构建或缺少签名清单时返回 null。<br/>
    ///     Nightly 的签名版本取自标题（<c>Nightly 2026-11-13 · 9f42064</c> → <c>nightly-2026-11-13-9f42064</c>），用
    ///     <c>SHA256SUMS.v2.sig</c>；正式版的签名版本是标签本身，用 <c>SHA256SUMS.sig</c>。
    /// </summary>
    public static HorizonRemoteBuild? TryParse(JsonObject release, IReadOnlyList<string> githubProxyPrefixes)
    {
        var tag = release["tag_name"]?.GetValue<string>();
        if (!IsGameTag(tag) || release["draft"]?.GetValue<bool>() == true) return null;
        var title = release["name"]?.GetValue<string>() ?? tag!;
        var nightly = tag == NightlyTag || tag!.StartsWith("nightly-", StringComparison.Ordinal);
        var version = nightly ? HorizonReleaseVerifier.NightlyVersionFromTitle(title) : tag;
        if (version is null) return null;

        var signatureName = nightly ? HorizonReleaseVerifier.NightlySignatureFileName : HorizonReleaseVerifier.ReleaseSignatureFileName;
        var archive = _PickArchive(release);
        var sums = _FindAsset(release, HorizonReleaseVerifier.SumsFileName);
        var signature = _FindAsset(release, signatureName);
        if (archive is null || sums is null || signature is null) return null;

        IReadOnlyList<string>? Urls(JsonObject asset)
        {
            var direct = asset["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(direct)) return null;
            return githubProxyPrefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(prefix => prefix.Trim().TrimEnd('/') + "/" + direct)
                .Append(direct)
                .ToList();
        }

        if (Urls(archive) is not { } archiveUrls || Urls(sums) is not { } sumsUrls || Urls(signature) is not { } signatureUrls)
            return null;
        return new HorizonRemoteBuild(
            version,
            title,
            archive["name"]!.GetValue<string>(),
            archive["size"]?.GetValue<long>() ?? 0,
            archiveUrls,
            sumsUrls,
            signatureUrls)
        {
            Tag = tag,
            SignatureFileName = signatureName,
            PublishedAt = DateTimeOffset.TryParse(release["published_at"]?.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal,
                out var published)
                ? published
                : null,
            Changelog = release["body"]?.GetValue<string>() ?? ""
        };
    }
}
