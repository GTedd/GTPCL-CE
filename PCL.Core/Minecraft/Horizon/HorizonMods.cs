using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>模组文件里有什么。</summary>
public enum HorizonModKind
{
    /// <summary>内容模组：<c>horizon-mod.toml</c> + <c>assets/</c>、<c>data/</c>，游戏启动时读取。</summary>
    Content,

    /// <summary>原生模组：<c>horizon-native.json</c> + <c>mixins/&lt;id&gt;/</c>，需要把游戏和它一起编译。</summary>
    Native,

    /// <summary>同一个 <c>.hmod</c> 里既有内容又有原生代码。</summary>
    ContentAndNative
}

/// <summary><c>horizon-native.json</c>（docs/api/mod-files.md）。</summary>
public sealed record HorizonNativeManifest(
    string Id,
    string Version,
    string Game,
    string Horizon,
    string Mixins,
    int? Hooks);

/// <summary>读出的模组信息。</summary>
public sealed record HorizonModInfo
{
    public required string Path { get; init; }
    public required bool IsFolder { get; init; }
    public required HorizonModKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>内容模组的 API 线（如 <c>0.2</c>）；纯原生模组为 null。</summary>
    public string? Api { get; init; }

    /// <summary><c>client</c>、<c>server</c> 或 <c>both</c>。</summary>
    public string Side { get; init; } = "client";

    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public HorizonNativeManifest? Native { get; init; }

    public bool HasNativeCode => Kind is HorizonModKind.Native or HorizonModKind.ContentAndNative;
}

/// <summary>
///     读取并校验 <c>.hmod</c>、<c>.hnmod</c> 与模组文件夹，规则见 Horizon 的 docs/api/mod-files.md 与 docs/mods.md。
///     只读不执行：原生模组的源码不会在这里编译或运行。
/// </summary>
public static partial class HorizonModFile
{
    public const string ContentManifest = "horizon-mod.toml";
    public const string NativeManifest = "horizon-native.json";
    public const string MixinManifest = "horizon-mixin.toml";

    public const long MaxFileSize = 64L * 1024 * 1024;
    private const int MaxNativeEntries = 512;
    private const long MaxNativeUnpacked = 64L * 1024 * 1024;
    private const int MaxContentFiles = 65536;
    private const long MaxContentFileSize = 16L * 1024 * 1024;
    private const long MaxContentTotal = 512L * 1024 * 1024;
    private const long MaxEmbeddedContentManifest = 1024 * 1024;

    private static readonly string[] _NativeManifestFields = ["schema", "id", "version", "game", "horizon", "mixins", "hooks"];

    private static readonly string[] _ContentManifestKeys =
        ["id", "name", "version", "description", "authors", "api", "side", "capabilities", "namespaces", "experimental"];

    private static readonly string[] _DocPrefixes = ["README", "LICENSE", "LICENCE", "COPYING", "NOTICE"];

    /// <summary>模组 id 规则：<c>[a-z0-9_]{1,64}</c>。</summary>
    public static bool IsValidId(string? id) => id is not null && _ModId().IsMatch(id);

    /// <summary>
    ///     读取一个模组文件或文件夹。
    /// </summary>
    /// <exception cref="InvalidDataException">不是合法的 Horizon 模组，消息为原因。</exception>
    public static HorizonModInfo Read(string path)
    {
        if (Directory.Exists(path)) return _ReadFolder(path);
        if (!File.Exists(path)) throw new FileNotFoundException("模组文件不存在", path);

        var length = new FileInfo(path).Length;
        if (length > MaxFileSize) throw new InvalidDataException($"文件超过 64 MiB（{length / 1024 / 1024} MiB）");

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"不是有效的 zip 文件：{ex.Message}", ex);
        }

        using (zip)
        {
            return _ReadZip(path, zip, path.EndsWith(".hnmod", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>不抛异常的 <see cref="Read" />。</summary>
    public static bool TryRead(string path, out HorizonModInfo? info, out string? error)
    {
        try
        {
            info = Read(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            info = null;
            error = ex.Message;
            return false;
        }
    }

    private static HorizonModInfo _ReadZip(string path, ZipArchive zip, bool nativeOnly)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/')) continue; // 目录项
            _CheckEntryPath(name);
            // ZipArchive 不暴露压缩方式；Deflate64 等方式在读取时会报错，这里只拦截 Unix 链接与设备
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode is not (0 or 0x8000))
                throw new InvalidDataException($"{name} 不是普通文件");
            if (!files.TryAdd(name, entry)) throw new InvalidDataException($"{name} 在压缩包里出现了不止一次");
        }

        var hasContent = files.ContainsKey(ContentManifest);
        var hasNative = files.ContainsKey(NativeManifest);
        if (!hasContent && !hasNative)
        {
            if (files.ContainsKey("mod.wasm"))
                throw new InvalidDataException("这是旧版 WebAssembly 模组，当前 Horizon 已不再运行它");
            throw new InvalidDataException($"缺少 {ContentManifest} 或 {NativeManifest}");
        }

        if (nativeOnly && hasContent)
            throw new InvalidDataException($".hnmod 只能包含原生代码，不能带 {ContentManifest}");

        HorizonNativeManifest? native = null;
        if (hasNative)
        {
            if (files.ContainsKey("mod.wasm"))
                throw new InvalidDataException("带原生代码的文件里不能有 mod.wasm");
            native = _ReadNativeManifest(_ReadText(files[NativeManifest], 1024 * 1024));
            _CheckNativeEntries(files, native, hasContent);
        }

        if (!hasContent)
        {
            return new HorizonModInfo
            {
                Path = path,
                IsFolder = false,
                Kind = HorizonModKind.Native,
                Id = native!.Id,
                Name = native.Id,
                Version = native.Version,
                Native = native
            };
        }

        var manifestEntry = files[ContentManifest];
        if (hasNative && manifestEntry.Length > MaxEmbeddedContentManifest)
            throw new InvalidDataException($"{ContentManifest} 超过 1 MiB");
        _CheckContentEntries(files, hasNative);
        var info = _ParseContentManifest(_ReadText(manifestEntry, MaxEmbeddedContentManifest), path, false);
        if (native is not null && native.Id != info.Id)
            throw new InvalidDataException($"{ContentManifest} 的 id（{info.Id}）与 {NativeManifest} 的 id（{native.Id}）不一致");
        return native is null ? info : info with { Kind = HorizonModKind.ContentAndNative, Native = native };
    }

    private static HorizonModInfo _ReadFolder(string folder)
    {
        var manifest = System.IO.Path.Combine(folder, ContentManifest);
        if (!File.Exists(manifest))
            throw new InvalidDataException($"文件夹里没有 {ContentManifest}");
        return _ParseContentManifest(File.ReadAllText(manifest, Encoding.UTF8), folder, true);
    }

    /// <summary>解析内容模组清单 <c>horizon-mod.toml</c>。</summary>
    public static HorizonModInfo ParseContentManifest(string toml, string path) => _ParseContentManifest(toml, path, false);

    private static HorizonModInfo _ParseContentManifest(string toml, string path, bool isFolder)
    {
        var table = HorizonToml.Parse(toml);
        var unknown = table.Keys.Except(_ContentManifestKeys).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"{ContentManifest} 有未知的键：{string.Join(", ", unknown)}");

        var id = HorizonToml.GetString(table, "id");
        if (!IsValidId(id)) throw new InvalidDataException($"{ContentManifest} 的 id 无效：{id}");
        var name = HorizonToml.GetString(table, "name");
        var version = HorizonToml.GetString(table, "version");
        var api = HorizonToml.GetString(table, "api");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"{ContentManifest} 缺少 name");
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException($"{ContentManifest} 缺少 version");
        if (string.IsNullOrWhiteSpace(api)) throw new InvalidDataException($"{ContentManifest} 缺少 api");

        return new HorizonModInfo
        {
            Path = path,
            IsFolder = isFolder,
            Kind = HorizonModKind.Content,
            Id = id!,
            Name = name,
            Version = version,
            Description = HorizonToml.GetString(table, "description") ?? string.Empty,
            Authors = HorizonToml.GetStrings(table, "authors"),
            Api = api,
            Side = HorizonToml.GetString(table, "side") ?? "client",
            Capabilities = HorizonToml.GetStrings(table, "capabilities")
        };
    }

    /// <summary>解析并校验 <c>horizon-native.json</c>：未知字段、id、提交号格式都按文档拒绝。</summary>
    public static HorizonNativeManifest ParseNativeManifest(string json) => _ReadNativeManifest(json);

    private static HorizonNativeManifest _ReadNativeManifest(string json)
    {
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException($"{NativeManifest} 不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{NativeManifest} 不是有效的 JSON：{ex.Message}", ex);
        }

        var unknown = obj.Select(p => p.Key).Except(_NativeManifestFields).ToList();
        if (unknown.Count > 0) throw new InvalidDataException($"{NativeManifest} 有未知字段：{string.Join(", ", unknown)}");

        if (obj["schema"] is not JsonValue schema || !schema.TryGetValue<int>(out var schemaValue) || schemaValue != 1)
            throw new InvalidDataException($"{NativeManifest} 的 schema 必须为 1");
        var id = _JsonString(obj, "id");
        if (!IsValidId(id)) throw new InvalidDataException($"{NativeManifest} 的 id 无效：{id}");
        var version = _JsonString(obj, "version");
        var game = _JsonString(obj, "game");
        if (string.IsNullOrEmpty(version)) throw new InvalidDataException($"{NativeManifest} 缺少 version");
        if (string.IsNullOrEmpty(game)) throw new InvalidDataException($"{NativeManifest} 缺少 game");
        var horizon = _JsonString(obj, "horizon");
        var mixins = _JsonString(obj, "mixins");
        if (horizon is null || !_CommitSha().IsMatch(horizon))
            throw new InvalidDataException($"{NativeManifest} 的 horizon 必须是 40 位小写十六进制提交号");
        if (mixins is null || !_CommitSha().IsMatch(mixins))
            throw new InvalidDataException($"{NativeManifest} 的 mixins 必须是 40 位小写十六进制提交号");

        int? hooks = null;
        if (obj["hooks"] is { } hooksNode)
        {
            if (hooksNode is not JsonValue hv || !hv.TryGetValue<int>(out var h) || h < 1)
                throw new InvalidDataException($"{NativeManifest} 的 hooks 必须是不小于 1 的整数");
            hooks = h;
        }

        return new HorizonNativeManifest(id!, version, game, horizon, mixins, hooks);
    }

    private static void _CheckNativeEntries(Dictionary<string, ZipArchiveEntry> files, HorizonNativeManifest manifest, bool hasContent)
    {
        var bundlePrefix = $"mixins/{manifest.Id}/";
        var mixinPath = bundlePrefix + MixinManifest;
        if (!files.TryGetValue(mixinPath, out var mixinEntry))
            throw new InvalidDataException($"缺少 {mixinPath}");

        var nativeCount = 0;
        long nativeSize = 0;
        foreach (var (name, entry) in files)
        {
            if (hasContent && _IsContentPath(name) || name == ContentManifest) continue;
            nativeCount++;
            nativeSize += entry.Length;
            if (name == NativeManifest) continue;
            if (!name.StartsWith(bundlePrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"原生模组里不允许出现 {name}");
            if (!_IsAllowedBundleFile(name[bundlePrefix.Length..]))
                throw new InvalidDataException($"原生模组的 {name} 不是允许的文件类型（只允许 .rs、.toml、data/ 下的文件与说明/许可文件）");
        }

        if (nativeCount > MaxNativeEntries) throw new InvalidDataException($"原生文件超过 {MaxNativeEntries} 个");
        if (nativeSize > MaxNativeUnpacked) throw new InvalidDataException("原生文件解压后超过 64 MiB");

        var mixin = HorizonToml.Parse(_ReadText(mixinEntry, 1024 * 1024));
        var mixinId = HorizonToml.GetString(mixin, "mod.id");
        var mixinVersion = HorizonToml.GetString(mixin, "mod.version");
        var revision = HorizonToml.GetString(mixin, "compatibility.horizon_revision");
        if (mixinId != manifest.Id)
            throw new InvalidDataException($"{MixinManifest} 的 [mod] id（{mixinId}）与 {NativeManifest} 不一致");
        if (mixinVersion != manifest.Version)
            throw new InvalidDataException($"{MixinManifest} 的 [mod] version（{mixinVersion}）与 {NativeManifest} 不一致");
        if (revision is not null && revision != manifest.Horizon)
            throw new InvalidDataException($"{MixinManifest} 的 horizon_revision 与 {NativeManifest} 的 horizon 不一致");
    }

    private static bool _IsAllowedBundleFile(string relative)
    {
        var parts = relative.Split('/');
        var fileName = parts[^1];
        if (fileName.EndsWith(".rs", StringComparison.Ordinal) || fileName.EndsWith(".toml", StringComparison.Ordinal))
            return true;
        // data/… 或顶层文件夹下的 data/…（engine/data/…）
        if (parts.Length >= 2 && parts[0] == "data") return true;
        if (parts.Length >= 3 && parts[1] == "data") return true;
        // 说明与许可文件：在包根目录或某个顶层文件夹里
        if (parts.Length <= 2 && (fileName == ".gitignore" || _DocPrefixes.Any(p => fileName.StartsWith(p, StringComparison.Ordinal))))
            return true;
        return false;
    }

    private static void _CheckContentEntries(Dictionary<string, ZipArchiveEntry> files, bool hasNative)
    {
        var count = 0;
        long total = 0;
        foreach (var (name, entry) in files)
        {
            if (name == ContentManifest || hasNative && (name == NativeManifest || name.StartsWith("mixins/", StringComparison.Ordinal)))
                continue;
            if (!_IsContentPath(name))
            {
                // 纯内容模组允许其他顶层文件（图标、说明等）；与原生代码同包时不允许
                if (hasNative) throw new InvalidDataException($"带原生代码的 .hmod 里不允许出现 {name}");
                continue;
            }

            count++;
            total += entry.Length;
            if (entry.Length > MaxContentFileSize) throw new InvalidDataException($"{name} 超过 16 MiB");
        }

        if (count > MaxContentFiles) throw new InvalidDataException($"内容文件超过 {MaxContentFiles} 个");
        if (total > MaxContentTotal) throw new InvalidDataException("内容文件总计超过 512 MiB");
    }

    private static bool _IsContentPath(string name) =>
        (name.StartsWith("assets/", StringComparison.Ordinal) || name.StartsWith("data/", StringComparison.Ordinal)) &&
        _ContentPath().IsMatch(name);

    private static void _CheckEntryPath(string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':'))
            throw new InvalidDataException($"压缩包里有不安全的路径：{name}");
        foreach (var part in name.Split('/'))
        {
            if (part is "" or "." or "..")
                throw new InvalidDataException($"压缩包里有不安全的路径：{name}");
        }
    }

    private static string _ReadText(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException($"{entry.FullName} 太大");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? _JsonString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex _ModId();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex _CommitSha();

    [GeneratedRegex("^(assets|data)(/[a-z0-9_.-]+){2,}$")]
    private static partial Regex _ContentPath();
}
