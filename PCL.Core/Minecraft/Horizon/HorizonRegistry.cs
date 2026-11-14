using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>注册表项目的种类（index.json 的 <c>kind</c>）。</summary>
public enum HorizonRegistryKind
{
    Mod,
    NativeMod,
    Modpack,
    ResourcePack,
    Shader,
    ShaderPack,
    DataPack,
    Other
}

public sealed record HorizonRegistryFile(string Name, string Url, long Size, string Sha256, string? Signature);

public sealed record HorizonRegistryRequire(string Id, string Version, string Sha256);

public sealed record HorizonRegistryBroken(string Horizon, string Code);

public sealed record HorizonRegistryVersion(
    string Version,
    string Published,
    string Changelog,
    string Api,
    string? Side,
    IReadOnlyList<string> Capabilities,
    int? ApiLevel,
    int? Hooks,
    string Game,
    IReadOnlyList<HorizonRegistryBroken> BrokenOn,
    IReadOnlyList<HorizonRegistryRequire> Requires,
    HorizonRegistryFile File)
{
    /// <summary>原生模组钉住的 Horizon 提交（<c>native-1/&lt;horizon&gt;/&lt;mixins&gt;</c> 的第二段）。</summary>
    public string? NativePin => Api.StartsWith("native-1/", StringComparison.Ordinal) ? Api.Split('/').ElementAtOrDefault(1) : null;

    /// <summary>
    ///     旧版 WebAssembly 模组：<c>.hmod</c> 带有 <c>registries</c> 以外的能力。Horizon 已不再运行它们，浏览器不提供单独安装。
    /// </summary>
    public bool NeedsPort =>
        File.Name.EndsWith(".hmod", StringComparison.Ordinal) && Capabilities.Any(c => c != "registries");
}

public sealed record HorizonRegistryProject(
    string Id,
    HorizonRegistryKind Kind,
    string KindName,
    string Name,
    string Summary,
    string Description,
    IReadOnlyList<string> Authors,
    string License,
    string Source,
    string? Icon,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Screenshots,
    string? Key,
    IReadOnlyList<HorizonRegistryVersion> Versions,
    long Downloads,
    long Stars)
{
    public string Page => "https://playhorizon.dev/mods/" + Id;

    /// <summary>Horizon 官方出品。</summary>
    public bool IsOfficial =>
        Authors.Count == 1 && Authors[0] == "Horizon" && Source.StartsWith("https://github.com/horizon-rust/", StringComparison.Ordinal);

    public string Updated => Versions.Select(v => v.Published).DefaultIfEmpty("").Max(StringComparer.Ordinal) ?? "";
}

public sealed record HorizonRegistryRevoked(string Id, string? Sha256, string Reason, string Date);

/// <summary>
///     Horizon 模组注册表的索引（docs/api/mod-index.md，schema 1）。
///     解析时按官方启动器的 <c>Index::check</c> 校验：任意一个项目不合法，整个索引都被拒绝。
/// </summary>
public sealed partial class HorizonRegistryIndex
{
    public const string IndexUrl = "https://horizon-rust.github.io/horizon-mods/index.json";
    public const int SupportedSchema = 1;
    public const long MaxIndexSize = 16L << 20;
    public const long MaxFileSize = 64L << 20;

    public int Schema { get; private init; }
    public string Generated { get; private init; } = "";
    public IReadOnlyList<HorizonRegistryProject> Projects { get; private init; } = [];
    public IReadOnlyList<HorizonRegistryRevoked> Revoked { get; private init; } = [];

    public HorizonRegistryProject? Find(string id) => Projects.FirstOrDefault(p => p.Id == id);

    /// <summary>文件是否被撤回：id 相同，且撤回项没写 sha256（整个项目）或 sha256 相同。</summary>
    public HorizonRegistryRevoked? FindRevoked(string id, string? sha256) =>
        Revoked.FirstOrDefault(r => r.Id == id &&
                                    (r.Sha256 is null || sha256 is not null && string.Equals(r.Sha256, sha256, StringComparison.OrdinalIgnoreCase)));

    /// <exception cref="InvalidDataException">不是索引、schema 过新或有不合法的项目。</exception>
    public static HorizonRegistryIndex Parse(string json, bool local = false)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("not a mod index");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("not a mod index: " + ex.Message, ex);
        }

        var schema = root["schema"] is JsonValue sv && sv.TryGetValue<int>(out var s)
            ? s
            : throw new InvalidDataException("not a mod index: missing schema");
        if (schema > SupportedSchema)
            throw new InvalidDataException($"the mod index is schema {schema}; this build reads {SupportedSchema}: update Horizon");

        var projects = (root["projects"] as JsonArray ?? []).Select((node, i) => _Project(node, i)).ToList();
        var revoked = (root["revoked"] as JsonArray ?? []).Select(_Revoked).ToList();
        var index = new HorizonRegistryIndex
        {
            Schema = schema,
            Generated = _Str(root, "generated") ?? "",
            Projects = projects,
            Revoked = revoked
        };
        index._Check(local);
        return index;
    }

    private void _Check(bool local)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Projects)
        {
            string Fail(string message) => throw new InvalidDataException($"mod index: project {p.Id}: {message}");

            if (!HorizonModFile.IsValidId(p.Id)) Fail("id is not [a-z0-9_]{1,64}");
            if (!ids.Add(p.Id)) Fail("listed twice");
            if (string.IsNullOrWhiteSpace(p.Name)) Fail("no name");
            if (string.IsNullOrWhiteSpace(p.License)) Fail("no license");
            if (!_Https(p.Source)) Fail("source is not an https URL");
            if (p.Icon is not null && !_Https(p.Icon)) Fail("icon is not an https URL");
            if (p.Screenshots.Count > 8) Fail("more than 8 screenshots");
            if (p.Screenshots.Any(u => !_Https(u))) Fail("screenshot is not an https URL");
            if (p.Key is not null)
            {
                try
                {
                    HorizonMinisign.ParsePublicKey(p.Key);
                }
                catch (FormatException ex)
                {
                    Fail(ex.Message);
                }
            }

            if (p.Versions.Count == 0) Fail("no versions");
            var versions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in p.Versions)
            {
                if (string.IsNullOrWhiteSpace(v.Version) || !versions.Add(v.Version)) Fail($"version {v.Version} is empty or listed twice");
                if (!_PlainName(v.File.Name)) Fail($"file name {v.File.Name} is not plain");
                var ext = ExtensionOf(p.Kind);
                if (ext is not null && !v.File.Name.EndsWith("." + ext, StringComparison.Ordinal) &&
                    !(p.Kind == HorizonRegistryKind.NativeMod && v.File.Name.EndsWith(".hmod", StringComparison.Ordinal)))
                    Fail($"file {v.File.Name} is not a .{ext}");
                if (!_Https(v.File.Url) && !(local && v.File.Url.StartsWith("file:///", StringComparison.Ordinal)))
                    Fail("file url is not https");
                if (!_Hex64().IsMatch(v.File.Sha256)) Fail("sha256 is not 64 lowercase hex digits");
                if (v.File.Size < 1 || v.File.Size > MaxFileSize) Fail("file size out of range");
                switch (p.Key, v.File.Signature)
                {
                    case (not null, null): Fail("unsigned, but the project has a key"); break;
                    case (null, not null): Fail("signed, but the project has no key"); break;
                    case (not null, not null):
                        try
                        {
                            HorizonMinisign.ParseSignature(v.File.Signature);
                        }
                        catch (FormatException ex)
                        {
                            Fail(ex.Message);
                        }

                        break;
                }
            }
        }

        foreach (var p in Projects)
        foreach (var v in p.Versions)
        foreach (var r in v.Requires)
        {
            if (Find(r.Id)?.Versions.FirstOrDefault(x => x.Version == r.Version)?.File.Sha256 != r.Sha256)
                throw new InvalidDataException($"mod index: project {p.Id}: requires {r.Id} {r.Version}, which isn't listed");
        }

        if (Revoked.Any(r => r.Sha256 is not null && !_Hex64().IsMatch(r.Sha256)))
            throw new InvalidDataException("mod index: revoked sha256 is not 64 lowercase hex digits");
    }

    public static HorizonRegistryKind KindOf(string kind) => kind switch
    {
        "mod" => HorizonRegistryKind.Mod,
        "native-mod" => HorizonRegistryKind.NativeMod,
        "modpack" => HorizonRegistryKind.Modpack,
        "resourcepack" => HorizonRegistryKind.ResourcePack,
        "shader" => HorizonRegistryKind.Shader,
        "shaderpack" => HorizonRegistryKind.ShaderPack,
        "datapack" => HorizonRegistryKind.DataPack,
        _ => HorizonRegistryKind.Other
    };

    public static string? ExtensionOf(HorizonRegistryKind kind) => kind switch
    {
        HorizonRegistryKind.Mod => "hmod",
        HorizonRegistryKind.NativeMod => "hnmod",
        HorizonRegistryKind.Modpack => "hpack",
        HorizonRegistryKind.ResourcePack or HorizonRegistryKind.ShaderPack or HorizonRegistryKind.DataPack => "zip",
        HorizonRegistryKind.Shader => "hshader",
        _ => null
    };

    private static HorizonRegistryProject _Project(JsonNode? node, int index)
    {
        if (node is not JsonObject o) throw new InvalidDataException($"mod index: project {index} is not an object");
        var kindName = _Req(o, "kind");
        return new HorizonRegistryProject(
            _Req(o, "id"),
            KindOf(kindName),
            kindName,
            _Req(o, "name"),
            _Req(o, "summary"),
            _Str(o, "description") ?? "",
            _Strs(o, "authors"),
            _Req(o, "license"),
            _Req(o, "source"),
            _Str(o, "icon"),
            _Strs(o, "categories"),
            _Strs(o, "tags"),
            _Strs(o, "screenshots"),
            _Str(o, "key"),
            (o["versions"] as JsonArray ?? throw new InvalidDataException($"mod index: project {index}: no versions"))
            .Select(_Version).ToList(),
            _Long(o, "downloads") ?? 0,
            _Long(o, "stars") ?? 0);
    }

    private static HorizonRegistryVersion _Version(JsonNode? node)
    {
        if (node is not JsonObject o) throw new InvalidDataException("mod index: version is not an object");
        var file = o["file"] as JsonObject ?? throw new InvalidDataException("mod index: version without file");
        return new HorizonRegistryVersion(
            _Req(o, "version"),
            _Str(o, "published") ?? "",
            _Str(o, "changelog") ?? "",
            _Req(o, "api"),
            _Str(o, "side"),
            _Strs(o, "capabilities"),
            (int?)_Long(o, "api_level"),
            (int?)_Long(o, "hooks"),
            _Req(o, "game"),
            (o["broken_on"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(b => new HorizonRegistryBroken(_Str(b, "horizon") ?? "", _Str(b, "code") ?? ""))
            .ToList(),
            (o["requires"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(r => new HorizonRegistryRequire(_Req(r, "id"), _Req(r, "version"), _Req(r, "sha256")))
            .ToList(),
            new HorizonRegistryFile(_Req(file, "name"), _Req(file, "url"),
                _Long(file, "size") ?? throw new InvalidDataException("mod index: file without size"),
                _Req(file, "sha256"), _Str(file, "signature")));
    }

    private static HorizonRegistryRevoked _Revoked(JsonNode? node) =>
        node is JsonObject o
            ? new HorizonRegistryRevoked(_Req(o, "id"), _Str(o, "sha256"), _Req(o, "reason"), _Str(o, "date") ?? "")
            : throw new InvalidDataException("mod index: revoked entry is not an object");

    private static string _Req(JsonObject o, string key) =>
        _Str(o, key) ?? throw new InvalidDataException($"mod index: missing {key}");

    private static string? _Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? _Long(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static IReadOnlyList<string> _Strs(JsonObject o, string key) =>
        o[key] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];

    private static bool _Https(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) && url.Length > 8 && !url.Any(char.IsWhiteSpace);

    private static bool _PlainName(string name) =>
        name.Length is > 0 and <= 128 && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-');

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex _Hex64();
}

/// <summary>浏览器里按什么分组（官方启动器的 browser Kind）。</summary>
public enum HorizonBrowseKind
{
    Mod,
    ResourcePack,
    Shader,
    DataPack,
    Modpack
}

public enum HorizonRegistrySort
{
    Downloads,
    Newest,
    Updated
}

/// <summary>
///     获取、缓存与搜索注册表索引，规则与官方启动器的浏览器相同。
/// </summary>
public static class HorizonRegistry
{
    /// <summary>本启动器支持的 Minecraft 版本（索引里的 <c>game</c>）。</summary>
    public const string Game = "26.3";

    private static readonly TimeSpan _Fresh = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim _Lock = new(1, 1);
    private static (DateTime Time, HorizonRegistryIndex Index)? _loaded;

    public static IReadOnlyList<string> ModCategories { get; } =
    [
        "adventure", "decoration", "economy", "equipment", "food", "game-mechanics", "library", "magic", "management",
        "minigame", "mobs", "optimization", "social", "storage", "technology", "transportation", "utility", "worldgen"
    ];

    public static HorizonBrowseKind? BrowseKindOf(HorizonRegistryKind kind) => kind switch
    {
        HorizonRegistryKind.Mod or HorizonRegistryKind.NativeMod => HorizonBrowseKind.Mod,
        HorizonRegistryKind.ResourcePack => HorizonBrowseKind.ResourcePack,
        HorizonRegistryKind.Shader or HorizonRegistryKind.ShaderPack => HorizonBrowseKind.Shader,
        HorizonRegistryKind.DataPack => HorizonBrowseKind.DataPack,
        HorizonRegistryKind.Modpack => HorizonBrowseKind.Modpack,
        _ => null
    };

    /// <summary>
    ///     取得索引：10 分钟内复用内存中的副本；网络失败时读本地缓存 <paramref name="cacheFile" />。
    /// </summary>
    public static async Task<HorizonRegistryIndex> GetIndexAsync(
        HttpClient http,
        string cacheFile,
        bool force = false,
        CancellationToken token = default)
    {
        await _Lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!force && _loaded is { } loaded && DateTime.UtcNow - loaded.Time < _Fresh) return loaded.Index;

            HorizonRegistryIndex index;
            try
            {
                using var response = await http.GetAsync(HorizonRegistryIndex.IndexUrl, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"The mod registry answered {(int)response.StatusCode}.");
                if (response.Content.Headers.ContentLength > HorizonRegistryIndex.MaxIndexSize)
                    throw new InvalidDataException("the mod index is larger than 16 MiB");
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                index = HorizonRegistryIndex.Parse(text);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    await File.WriteAllTextAsync(cacheFile, text, new UTF8Encoding(false), token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // 缓存写不进去不影响本次使用
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException } or IOException)
            {
                if (!File.Exists(cacheFile)) throw;
                index = HorizonRegistryIndex.Parse(await File.ReadAllTextAsync(cacheFile, token).ConfigureAwait(false));
            }

            _loaded = (DateTime.UtcNow, index);
            return index;
        }
        finally
        {
            _Lock.Release();
        }
    }

    /// <summary>
    ///     搜索：种类匹配；查询的每个词（小写）都要出现在 “id 名称 简介 作者 标签” 里；分类都要命中；必须有 26.3 的版本。
    /// </summary>
    public static List<HorizonRegistryProject> Search(
        HorizonRegistryIndex index,
        HorizonBrowseKind kind,
        string? query,
        IReadOnlyCollection<string>? categories = null,
        HorizonRegistrySort sort = HorizonRegistrySort.Downloads)
    {
        var words = (query ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = index.Projects.Where(p =>
        {
            if (BrowseKindOf(p.Kind) != kind) return false;
            var hay = $"{p.Id} {p.Name} {p.Summary} {string.Join(' ', p.Authors)} {string.Join(' ', p.Tags)}".ToLowerInvariant();
            if (!words.All(hay.Contains)) return false;
            if (categories is { Count: > 0 } && !categories.All(p.Categories.Contains)) return false;
            var forGame = p.Versions.Where(v => v.Game == Game).ToList();
            return forGame.Count > 0 && !forGame.All(v => v.NeedsPort);
        });

        return sort switch
        {
            HorizonRegistrySort.Newest => result
                .OrderByDescending(p => p.Versions.Select(v => v.Published).DefaultIfEmpty("").Min(StringComparer.Ordinal), StringComparer.Ordinal)
                .ThenByDescending(p => p.Downloads).ToList(),
            HorizonRegistrySort.Updated => result.OrderByDescending(p => p.Updated, StringComparer.Ordinal)
                .ThenByDescending(p => p.Downloads).ToList(),
            _ => result.OrderByDescending(p => p.Downloads).ToList()
        };
    }

    /// <summary>为什么这个版本不能装；能装时返回 null。</summary>
    public static string? BlockedReason(HorizonRegistryIndex index, HorizonRegistryProject project, HorizonRegistryVersion version,
        HorizonContentSupport support)
    {
        if (version.Game != Game) return $"为 Minecraft {version.Game} 制作";
        if (index.FindRevoked(project.Id, version.File.Sha256) is { } revoked) return $"已被撤回：{revoked.Reason}";
        if (version.NeedsPort) return "旧版 WebAssembly 模组，Horizon 已不再运行";
        switch (project.Kind)
        {
            case HorizonRegistryKind.Mod when !support.SupportsModApi(version.Api):
                return $"需要模组 API {version.Api}，当前构建支持 {string.Join(", ", support.ModApis)}";
            case HorizonRegistryKind.Shader when version.Api != "0.1":
                return $"光影 API {version.Api} 暂不支持";
        }

        return null;
    }

    /// <summary>最适合安装的版本：最新、未被撤回、当前构建能用的那个。原生模组优先选与当前提交相符的。</summary>
    public static HorizonRegistryVersion? PickVersion(HorizonRegistryIndex index, HorizonRegistryProject project,
        HorizonContentSupport support, string? playedRevision)
    {
        var usable = project.Versions.Where(v => BlockedReason(index, project, v, support) is null).ToList();
        if (project.Kind == HorizonRegistryKind.NativeMod && playedRevision is not null)
        {
            // playedRevision 可以是短提交号：按前缀比较
            var fits = usable.FirstOrDefault(v =>
                v.Hooks is { } hooks
                    ? support.SupportsNativeHooks(hooks)
                    : v.NativePin?.StartsWith(playedRevision, StringComparison.Ordinal) == true);
            if (fits is not null) return fits;
        }

        return usable.FirstOrDefault();
    }
}
