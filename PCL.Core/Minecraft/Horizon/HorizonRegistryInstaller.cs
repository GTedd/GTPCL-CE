using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>浏览器装过的一项（<c>mod-browser.json</c> 的 entries，与官方启动器同一份文件、同样的字段）。</summary>
public sealed record HorizonBrowserEntry(
    string Project,
    string Title,
    string Kind,
    string Version,
    string File,
    string Checksum,
    string? Verdict)
{
    public string Sha256 => Checksum.StartsWith("SHA-256:", StringComparison.Ordinal) ? Checksum[8..] : Checksum;
}

/// <summary>
///     从 Horizon 注册表安装、更新、删除内容，检查与官方启动器的 <c>index::check_file</c> 相同（docs/api/mod-index.md）：
///     来源地址、大小、SHA-256、minisign 签名、撤回列表、文件清单与索引是否一致。
/// </summary>
public static partial class HorizonRegistryInstaller
{
    public const string RecordFile = "mod-browser.json";

    private static readonly string[] _AllowedHosts =
    [
        "https://cdn.modrinth.com/",
        "https://edge.forgecdn.net/",
        "https://mediafilez.forgecdn.net/",
        "https://github.com/horizon-rust/horizon-mods/releases/download/",
        "https://github.com/ludovic111/horizon-mods/releases/download/"
    ];

    private static readonly string[] _RecordFolders = ["mods", "resourcepacks", "shaderpacks", "horizon-mods", "horizon-native-mods"];
    private static readonly object _RecordLock = new();

    /// <summary>浏览器记录的种类名。</summary>
    public static string EntryKind(HorizonRegistryKind kind) => kind switch
    {
        HorizonRegistryKind.Mod or HorizonRegistryKind.NativeMod => "mod",
        HorizonRegistryKind.ResourcePack => "resource-pack",
        HorizonRegistryKind.Shader or HorizonRegistryKind.ShaderPack => "shader",
        HorizonRegistryKind.DataPack => "data-pack",
        HorizonRegistryKind.Modpack => "modpack",
        _ => "other"
    };

    /// <summary>装到游戏目录里的相对路径（<c>/</c> 分隔）。数据包与整合包不在这里安装。</summary>
    public static string RelativePath(HorizonRegistryProject project, HorizonRegistryVersion version) => project.Kind switch
    {
        HorizonRegistryKind.Mod => $"horizon-mods/{project.Id}.hmod",
        HorizonRegistryKind.NativeMod => $"horizon-native-mods/{project.Id}.{(version.File.Name.EndsWith(".hmod", StringComparison.Ordinal) ? "hmod" : "hnmod")}",
        HorizonRegistryKind.ResourcePack => $"resourcepacks/{version.File.Name}",
        HorizonRegistryKind.Shader or HorizonRegistryKind.ShaderPack => $"shaderpacks/{version.File.Name}",
        _ => throw new NotSupportedException($"暂不支持从浏览器安装{project.KindName}")
    };

    /// <summary>下载地址是否在允许的来源：Modrinth、CurseForge、Horizon 注册表，或某个仓库的 GitHub Release 附件。</summary>
    public static bool IsAllowedUrl(string url) =>
        _AllowedHosts.Any(h => url.StartsWith(h, StringComparison.Ordinal)) || _GithubReleaseAsset().IsMatch(url);

    /// <summary>读取 <c>mod-browser.json</c>，只保留指向允许文件夹内的项。</summary>
    public static List<HorizonBrowserEntry> ReadEntries(string gameDir)
    {
        var result = new List<HorizonBrowserEntry>();
        foreach (var o in _ReadRaw(gameDir))
        {
            if (o["source"]?.GetValue<string>() is not "horizon") continue;
            var file = o["file"]?.GetValue<string>();
            if (file is null || !_InRecordFolders(file)) continue;
            result.Add(new HorizonBrowserEntry(
                o["project"]?.GetValue<string>() ?? "",
                o["title"]?.GetValue<string>() ?? "",
                o["kind"]?.GetValue<string>() ?? "",
                o["version"]?.GetValue<string>() ?? "",
                file,
                o["checksum"]?.GetValue<string>() ?? "",
                o["verdict"]?.GetValue<string>()));
        }

        return result;
    }

    /// <summary>
    ///     安装一个版本：下载、检查，然后移到游戏目录并记录；模组装好后打开。同一版本已装好时什么也不做。
    /// </summary>
    /// <returns>装好的记录。</returns>
    public static async Task<HorizonBrowserEntry> InstallAsync(
        HttpClient http,
        HorizonRegistryIndex index,
        HorizonRegistryProject project,
        HorizonRegistryVersion version,
        string gameDir,
        IReadOnlyList<string> githubProxies,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        if (index.FindRevoked(project.Id, version.File.Sha256) is { } revoked)
            throw new InvalidOperationException($"{project.Name} 已被撤回：{revoked.Reason}");
        var relative = RelativePath(project, version);
        var target = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));

        var entries = ReadEntries(gameDir);
        var old = entries.FirstOrDefault(e => e.Project == project.Id);
        if (old is not null && old.Version == version.Version && File.Exists(Path.Combine(gameDir, old.File))) return old;
        if (entries.FirstOrDefault(e => e.File == relative && e.Project != project.Id) is { } owner)
            throw new IOException($"{relative} 属于 {owner.Title}：请先删除它");
        var clash = File.Exists(target) && (old is null || old.File != relative);
        if (clash || project.Kind == HorizonRegistryKind.Mod && Directory.Exists(Path.Combine(gameDir, "horizon-mods", project.Id)))
            throw new IOException(project.Kind is HorizonRegistryKind.Mod or HorizonRegistryKind.NativeMod
                ? $"游戏目录里已经有 id 为 {project.Id} 的模组（不是从浏览器装的），请先删除或移走它"
                : $"{relative} 已经存在，请先删除或移走它");

        var staging = Path.Combine(gameDir, "mod-browser", "downloads");
        Directory.CreateDirectory(staging);
        var downloaded = Path.Combine(staging, version.File.Name);
        try
        {
            await DownloadAsync(http, version.File, downloaded, githubProxies, progress, token).ConfigureAwait(false);
            var verdict = CheckFile(index, project, version, downloaded);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(downloaded, target, true);
            if (old is not null && old.File != relative)
            {
                // 改为原生模组的内容模组保留原 .hmod，直到原生构建里有了它
                var keep = project.Kind == HorizonRegistryKind.NativeMod && old.File.StartsWith("horizon-mods/", StringComparison.Ordinal);
                var oldPath = Path.Combine(gameDir, old.File);
                if (!keep && File.Exists(oldPath)) File.Delete(oldPath);
            }

            var entry = new HorizonBrowserEntry(project.Id, project.Name, EntryKind(project.Kind), version.Version, relative,
                "SHA-256:" + version.File.Sha256, verdict);
            _SaveEntry(gameDir, entry, project);
            if (project.Kind is HorizonRegistryKind.Mod or HorizonRegistryKind.NativeMod)
                HorizonGameContent.SetEnabled(gameDir, project.Id, true);
            return entry;
        }
        finally
        {
            if (File.Exists(downloaded)) File.Delete(downloaded);
        }
    }

    /// <summary>删除浏览器装的一项：文件、开关与记录。</summary>
    public static void Remove(string gameDir, HorizonBrowserEntry entry)
    {
        var path = Path.Combine(gameDir, entry.File);
        if (File.Exists(path)) File.Delete(path);
        if (entry.Kind == "mod")
        {
            if (entry.File.StartsWith("horizon-native-mods/", StringComparison.Ordinal))
            {
                var content = Path.Combine(gameDir, "horizon-mods", entry.Project + ".hmod");
                if (File.Exists(content)) File.Delete(content);
            }

            HorizonGameContent.SetEnabled(gameDir, entry.Project, false);
        }

        lock (_RecordLock)
        {
            var raw = _ReadRaw(gameDir).Where(o => !(o["source"]?.GetValue<string>() == "horizon" &&
                                                    o["project"]?.GetValue<string>() == entry.Project)).ToList();
            _WriteRaw(gameDir, raw);
        }
    }

    /// <summary>
    ///     流式下载：先检查来源地址，边写边算 SHA-256，大小与哈希都对上才改名为正式文件。
    ///     注册表的 GitHub 文件可经加速站下载（哈希由索引固定，加速站无法篡改）。
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, HorizonRegistryFile file, string destination,
        IReadOnlyList<string> githubProxies, IProgress<double>? progress, CancellationToken token)
    {
        if (!IsAllowedUrl(file.Url))
            throw new InvalidOperationException($"{file.Url} 不在 Modrinth、CurseForge、Horizon 的文件服务器或 GitHub Release 上，已拒绝下载");
        if (!_SafeName(file.Name)) throw new InvalidDataException($"文件名 {file.Name} 不安全");

        var urls = file.Url.StartsWith("https://github.com/", StringComparison.Ordinal)
            ? githubProxies.Select(p => p.TrimEnd('/') + "/" + file.Url).Prepend(file.Url).ToList()
            : [file.Url];
        Exception? last = null;
        foreach (var url in urls)
        {
            var part = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + ".part");
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = File.Create(part))
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > file.Size) throw new InvalidDataException("下载的文件比索引写的大");
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        progress?.Report((double)total / file.Size);
                    }

                    if (total != file.Size) throw new InvalidDataException($"只下载到 {total} / {file.Size} 字节");
                    if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA-256 与索引不符：文件不是索引列出的那个，什么也没有安装");
                }

                File.Move(part, destination, true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException { InnerException: TimeoutException })
            {
                last = ex;
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
            }
        }

        throw new InvalidOperationException($"下载 {file.Name} 失败：{last?.Message}", last);
    }

    /// <summary>
    ///     检查下载好的文件（大小、SHA-256、撤回、签名、清单与索引一致）。
    /// </summary>
    /// <returns><c>Verified</c>（作者签名已校验）或 <c>Unsigned</c>（只校验了哈希）。</returns>
    /// <exception cref="InvalidDataException">任何一项不符。</exception>
    public static string CheckFile(HorizonRegistryIndex index, HorizonRegistryProject project, HorizonRegistryVersion version, string path)
    {
        var length = new FileInfo(path).Length;
        if (length != version.File.Size) throw new InvalidDataException($"{length} 字节，索引写的是 {version.File.Size}");
        string sha;
        using (var stream = File.OpenRead(path)) sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (sha != version.File.Sha256) throw new InvalidDataException("SHA-256 与索引不符：什么也没有安装");
        if (index.FindRevoked(project.Id, sha) is { } revoked) throw new InvalidDataException($"{project.Name} 已被撤回：{revoked.Reason}");

        string verdict;
        switch (project.Key, version.File.Signature)
        {
            case (not null, not null):
                bool ok;
                try
                {
                    ok = HorizonMinisign.VerifyFile(project.Key, version.File.Signature, path);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("签名：" + ex.Message, ex);
                }

                if (!ok) throw new InvalidDataException("签名与项目的公钥不符");
                verdict = "Verified";
                break;
            case (null, null):
                verdict = "Unsigned";
                break;
            default:
                throw new InvalidDataException("签名与项目的公钥不配套");
        }

        _CheckFacts(project, version, path);
        return verdict;
    }

    private static void _CheckFacts(HorizonRegistryProject project, HorizonRegistryVersion version, string path)
    {
        switch (project.Kind)
        {
            case HorizonRegistryKind.Mod:
            {
                var info = HorizonModFile.Read(path);
                if (info.HasNativeCode) throw new InvalidDataException("它带有原生代码（horizon-native.json），应是原生模组");
                var same = info.Id == project.Id && info.Version == version.Version && info.Api == version.Api &&
                           info.Side == (version.Side ?? "client") &&
                           info.Capabilities.Order().SequenceEqual(version.Capabilities.Order());
                if (!same)
                    throw new InvalidDataException(
                        $"它的 horizon-mod.toml（{info.Id} {info.Version}，API {info.Api}，{info.Side}，[{string.Join(", ", info.Capabilities)}]）与索引不符");
                break;
            }
            case HorizonRegistryKind.NativeMod:
            {
                var native = HorizonModFile.Read(path).Native ?? throw new InvalidDataException("它不含原生代码");
                if (native.Id != project.Id) throw new InvalidDataException($"它是 {native.Id}，不是 {project.Id}");
                var api = $"native-1/{native.Horizon}/{native.Mixins}";
                if (native.Version != version.Version || api != version.Api || native.Game != version.Game ||
                    version.Hooks is not null && native.Hooks != version.Hooks)
                    throw new InvalidDataException("它的 horizon-native.json 与索引不符");
                break;
            }
            case HorizonRegistryKind.ResourcePack:
            {
                var api = PackFormatFacts(path, requireData: false);
                if (api != version.Api) throw new InvalidDataException($"它的资源包格式 {api} 与索引不符");
                break;
            }
            case HorizonRegistryKind.Shader:
            {
                var (id, shaderVersion, api) = HorizonPacks.ReadShaderManifest(path)
                                               ?? throw new InvalidDataException("它不是 Horizon 光影包");
                if (id != project.Id || shaderVersion != version.Version || api != version.Api)
                    throw new InvalidDataException($"它的 horizon-shaders.toml（{id} {shaderVersion}，光影 API {api}）与索引不符");
                if (api != "0.1") throw new InvalidDataException($"光影 API {api} 暂不支持");
                break;
            }
            case HorizonRegistryKind.ShaderPack:
            {
                using var zip = ZipFile.OpenRead(path);
                if (!zip.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.Ordinal)))
                    throw new InvalidDataException("压缩包里没有 shaders 文件夹");
                break;
            }
            default:
                throw new InvalidDataException($"暂不支持安装{project.KindName}");
        }
    }

    /// <summary>
    ///     资源包 / 数据包的格式事实：<c>pack.mcmeta</c> 中 <c>pack.pack_format</c> 或 <c>pack.min_format</c>，
    ///     数字原样、数组用 <c>.</c> 连接（<c>[97,1]</c> → <c>97.1</c>）。
    /// </summary>
    public static string PackFormatFacts(string path, bool requireData)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Any(e => e.FullName.StartsWith('/') || e.FullName.Contains('\\') || e.FullName.Split('/').Contains("..")))
            throw new InvalidDataException("压缩包里有不安全的路径");
        var meta = zip.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("没有 pack.mcmeta");
        if (requireData && !zip.Entries.Any(e => e.FullName.StartsWith("data/", StringComparison.Ordinal)))
            throw new InvalidDataException("没有 data 文件夹");
        JsonNode? json;
        using (var reader = new StreamReader(meta.Open())) json = JsonNode.Parse(reader.ReadToEnd());
        var pack = json?["pack"] as JsonObject ?? throw new InvalidDataException("pack.mcmeta 没有 pack");
        var format = pack["pack_format"] ?? pack["min_format"];
        return format switch
        {
            null => "",
            JsonArray array => string.Join('.', array.Select(n => n?.ToJsonString())),
            _ => format.ToJsonString()
        };
    }

    private static void _SaveEntry(string gameDir, HorizonBrowserEntry entry, HorizonRegistryProject project)
    {
        lock (_RecordLock)
        {
            var raw = _ReadRaw(gameDir)
                .Where(o => !(o["source"]?.GetValue<string>() == "horizon" && o["project"]?.GetValue<string>() == entry.Project))
                .ToList();
            var obj = new JsonObject
            {
                ["source"] = "horizon",
                ["project"] = entry.Project,
                ["slug"] = entry.Project,
                ["title"] = entry.Title,
                ["kind"] = entry.Kind,
                ["version"] = entry.Version,
                ["number"] = entry.Version,
                ["file"] = entry.File,
                ["checksum"] = entry.Checksum,
                ["page"] = project.Page,
                ["dependency"] = false
            };
            if (entry.Verdict is not null) obj["verdict"] = entry.Verdict;
            raw.Add(obj);
            _WriteRaw(gameDir, raw);
        }
    }

    private static List<JsonObject> _ReadRaw(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, RecordFile);
            if (!File.Exists(path)) return [];
            return (JsonNode.Parse(File.ReadAllText(path))?["entries"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(o => (JsonObject)o.DeepClone())
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private static void _WriteRaw(string gameDir, List<JsonObject> entries)
    {
        var path = Path.Combine(gameDir, RecordFile);
        var temp = Path.Combine(gameDir, $".{RecordFile}.{Environment.ProcessId}.part");
        var json = new JsonObject { ["entries"] = new JsonArray(entries.Select(e => (JsonNode)e).ToArray()) };
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static bool _InRecordFolders(string file)
    {
        var parts = file.Split('/');
        return parts.Length >= 2 && _RecordFolders.Contains(parts[0]) && parts.All(p => p is not ("" or "." or ".."));
    }

    private static bool _SafeName(string name)
    {
        if (name.Length is 0 or > 200 || name[0] == '.' || name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c))) return false;
        if (name.Any(c => c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '﻿'))
            return false;
        var stem = name.Split('.')[0].Trim().ToUpperInvariant();
        return !_DeviceName().IsMatch(stem);
    }

    [GeneratedRegex(@"^https://github\.com/(?!\.{1,2}/)[A-Za-z0-9._+%-]+/(?!\.{1,2}/)[A-Za-z0-9._+%-]+/releases/download/(?!\.{1,2}/)[A-Za-z0-9._+%-]+/(?!\.{1,2}$)[A-Za-z0-9._+%-]+$")]
    private static partial Regex _GithubReleaseAsset();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$")]
    private static partial Regex _DeviceName();
}
