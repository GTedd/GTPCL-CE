using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Horizon;

public enum HorizonPackKind
{
    ResourcePack,

    /// <summary>Horizon 自己的光影包（<c>.hshader</c> 或带 <c>horizon-shaders.toml</c> 的文件夹）。</summary>
    HorizonShader,

    /// <summary><c>shaders/</c> 格式（OptiFine / Iris 风格）的光影包，Horizon 用自己的翻译器运行。</summary>
    ShadersFolderPack,

    DataPack
}

/// <summary>游戏目录里的一个包。</summary>
public sealed record HorizonPack(
    HorizonPackKind Kind,
    string Path,
    string File,
    string Title,
    string Description,
    bool Enabled,
    string? ShaderKey,
    string? Problem);

/// <summary>
///     Horizon 游戏目录里的光影包、资源包与数据包（docs/api/content.md），开关规则与官方启动器相同：
///     <list type="bullet">
///         <item>光影包：<c>horizon-graphics.txt</c> 的 <c>pickedShaderPack</c>，Horizon 光影包写 <c>horizon:&lt;id&gt;</c>，
///             其余写文件名，空为不用光影；同一时间只开一个。</item>
///         <item>资源包：<c>options.txt</c> 的 <c>resourcePacks</c>（JSON 数组，优先级低的在前），打开的放到最上面。</item>
///         <item>数据包：<c>saves/&lt;世界&gt;/datapacks.json</c> 的 enabled / disabled，两边都没有的视为打开。</item>
///     </list>
/// </summary>
public static partial class HorizonPacks
{
    public const string ResourcePacksDir = "resourcepacks";
    public const string ShaderPacksDir = "shaderpacks";
    public const string ShaderManifest = "horizon-shaders.toml";
    private const string GraphicsFile = "horizon-graphics.txt";
    private const string OptionsFile = "options.txt";
    private const string PickedShaderKey = "pickedShaderPack";
    private const long MetaLimit = 4L << 20;

    private static readonly JsonSerializerOptions _CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    #region key:value 文件

    /// <summary>读取 <c>key:value</c> 文件中第一行 <c>key:</c> 的值（原样，不去空格）。</summary>
    public static string? ReadLine(string file, string key)
    {
        if (!System.IO.File.Exists(file)) return null;
        foreach (var line in System.IO.File.ReadAllLines(file, Encoding.UTF8))
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return line[(key.Length + 1)..];
        return null;
    }

    /// <summary>把第一行 <c>key:</c> 改为 <c>key:value</c>，没有则追加；其他行原样保留，统一以 LF 结尾。</summary>
    public static void SetLine(string file, string key, string value)
    {
        var lines = System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file, Encoding.UTF8).ToList() : [];
        var index = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (index >= 0) lines[index] = $"{key}:{value}";
        else lines.Add($"{key}:{value}");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllText(file, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }

    #endregion

    #region 光影包

    /// <summary>读取 Horizon 光影包清单的 id、version、api；不是 Horizon 光影包或清单无效时返回 null。</summary>
    public static (string Id, string Version, string Api)? ReadShaderManifest(string path)
    {
        try
        {
            string text;
            if (Directory.Exists(path))
            {
                var manifest = System.IO.Path.Combine(path, ShaderManifest);
                if (!System.IO.File.Exists(manifest)) return null;
                text = System.IO.File.ReadAllText(manifest, Encoding.UTF8);
            }
            else
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry(ShaderManifest);
                if (entry is null || entry.Length > 1024 * 1024) return null;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                text = reader.ReadToEnd();
            }

            var table = HorizonToml.Parse(text);
            if (HorizonToml.GetLong(table, "schema") != 1) return null;
            var id = HorizonToml.GetString(table, "id");
            var version = HorizonToml.GetString(table, "version");
            var api = HorizonToml.GetString(table, "api");
            var name = HorizonToml.GetString(table, "name");
            if (!HorizonModFile.IsValidId(id) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(name) ||
                api is not ("0.1" or "0.2"))
                return null;
            return (id!, version!, api);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>列出 <c>shaderpacks/</c> 里的光影包，标出正在使用的那个。</summary>
    public static List<HorizonPack> ListShaders(string gameDir)
    {
        var dir = System.IO.Path.Combine(gameDir, ShaderPacksDir);
        if (!Directory.Exists(dir)) return [];
        var picked = _EnabledShaderKey(gameDir);
        var packs = new List<HorizonPack>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var file = System.IO.Path.GetFileName(path);
            if (file.StartsWith('.')) continue;
            var isFolder = Directory.Exists(path);
            var horizonPack = isFolder
                ? System.IO.File.Exists(System.IO.Path.Combine(path, ShaderManifest))
                : file.EndsWith(".hshader", StringComparison.OrdinalIgnoreCase);
            if (horizonPack)
            {
                var manifest = ReadShaderManifest(path);
                var key = manifest is { } m ? "horizon:" + m.Id : null;
                if (key is not null && packs.Any(p => p.ShaderKey == key)) continue; // 同 id 只列出文件名靠前的一个
                packs.Add(new HorizonPack(HorizonPackKind.HorizonShader, path, file, _ShaderTitle(path) ?? file,
                    manifest is { } mm ? $"Horizon 光影 · {mm.Version} · API {mm.Api}" : "Horizon 光影",
                    picked == key || picked == file, key,
                    manifest is null ? "horizon-shaders.toml 无效" : null));
                continue;
            }

            var shadersFormat = isFolder
                ? Directory.Exists(System.IO.Path.Combine(path, "shaders"))
                : file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (!shadersFormat) continue;
            packs.Add(new HorizonPack(HorizonPackKind.ShadersFolderPack, path, file, file, "shaders/ 格式", picked == file, null, null));
        }

        return packs.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? _ShaderTitle(string path)
    {
        try
        {
            string text;
            if (Directory.Exists(path)) text = System.IO.File.ReadAllText(System.IO.Path.Combine(path, ShaderManifest), Encoding.UTF8);
            else
            {
                using var zip = ZipFile.OpenRead(path);
                using var reader = new StreamReader(zip.GetEntry(ShaderManifest)!.Open(), Encoding.UTF8);
                text = reader.ReadToEnd();
            }

            return HorizonToml.GetString(HorizonToml.Parse(text), "name");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? _EnabledShaderKey(string gameDir)
    {
        var picked = ReadLine(System.IO.Path.Combine(gameDir, GraphicsFile), PickedShaderKey);
        if (string.IsNullOrEmpty(picked)) return null;
        var path = System.IO.Path.Combine(gameDir, ShaderPacksDir, picked);
        if (!picked.StartsWith("horizon:", StringComparison.Ordinal) && (System.IO.File.Exists(path) || Directory.Exists(path)) &&
            ReadShaderManifest(path) is { } m)
            return "horizon:" + m.Id;
        return picked;
    }

    /// <summary>
    ///     使用或取消光影包。使用时写入它的键（Horizon 光影包为 <c>horizon:&lt;id&gt;</c>），并把旧的选项文件
    ///     <c>shaderpacks/&lt;文件&gt;.txt</c> 复制为 <c>shaderpacks/.horizon-options/&lt;id&gt;.txt</c>（已有则不动）；取消时清空。
    /// </summary>
    public static void SetShaderEnabled(string gameDir, HorizonPack pack, bool enabled)
    {
        if (pack.ShaderKey is { } key && key.StartsWith("horizon:", StringComparison.Ordinal))
        {
            var target = System.IO.Path.Combine(gameDir, ShaderPacksDir, ".horizon-options", key[8..] + ".txt");
            var sidecar = pack.Path + ".txt";
            if (!System.IO.File.Exists(target) && System.IO.File.Exists(sidecar))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                System.IO.File.Copy(sidecar, target);
            }
        }

        SetLine(System.IO.Path.Combine(gameDir, GraphicsFile), PickedShaderKey, enabled ? pack.ShaderKey ?? pack.File : "");
    }

    #endregion

    #region 资源包

    /// <summary>列出 <c>resourcepacks/</c> 里有 <c>pack.mcmeta</c> 的资源包。</summary>
    public static List<HorizonPack> ListResourcePacks(string gameDir)
    {
        var dir = System.IO.Path.Combine(gameDir, ResourcePacksDir);
        if (!Directory.Exists(dir)) return [];
        var enabled = _ReadJsonList(System.IO.Path.Combine(gameDir, OptionsFile), "resourcePacks");
        var packs = new List<HorizonPack>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var file = System.IO.Path.GetFileName(path);
            if (file.StartsWith('.')) continue;
            var meta = ReadPackMeta(path);
            if (meta is null) continue;
            packs.Add(new HorizonPack(HorizonPackKind.ResourcePack, path, file, file, PlainText(meta["pack"]?["description"]),
                enabled.Contains("file/" + file), null, null));
        }

        return packs.OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>打开（放到最上面）或关闭资源包；关闭时也从 incompatibleResourcePacks 中移除。</summary>
    public static void SetResourcePackEnabled(string gameDir, HorizonPack pack, bool enabled)
    {
        var options = System.IO.Path.Combine(gameDir, OptionsFile);
        var id = "file/" + pack.File;
        var list = _ReadJsonList(options, "resourcePacks");
        list.Remove(id);
        if (list.Count == 0) list.Add("vanilla");
        if (enabled) list.Add(id);
        else
        {
            var incompatible = _ReadJsonList(options, "incompatibleResourcePacks");
            if (incompatible.Remove(id)) SetLine(options, "incompatibleResourcePacks", JsonSerializer.Serialize(incompatible, _CompactJson));
        }

        SetLine(options, "resourcePacks", JsonSerializer.Serialize(list, _CompactJson));
    }

    private static List<string> _ReadJsonList(string file, string key)
    {
        try
        {
            var value = ReadLine(file, key);
            if (string.IsNullOrWhiteSpace(value)) return [];
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>读取文件夹或压缩包根目录的 <c>pack.mcmeta</c>（最多 4 MiB）；没有或不是 JSON 时返回 null。</summary>
    public static JsonObject? ReadPackMeta(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var meta = System.IO.Path.Combine(path, "pack.mcmeta");
                if (!System.IO.File.Exists(meta) || new FileInfo(meta).Length > MetaLimit) return null;
                return JsonNode.Parse(System.IO.File.ReadAllText(meta)) as JsonObject;
            }

            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("pack.mcmeta");
            if (entry is null || entry.Length > MetaLimit) return null;
            using var reader = new StreamReader(entry.Open());
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>文本组件转纯文本：字符串、数组、对象（text 或 translate，再接 extra），去掉 § 代码，换行变空格。</summary>
    public static string PlainText(JsonNode? component)
    {
        var sb = new StringBuilder();

        void Walk(JsonNode? node, int depth)
        {
            if (node is null || depth > 32) return;
            switch (node)
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    sb.Append(s);
                    break;
                case JsonArray a:
                    foreach (var n in a) Walk(n, depth + 1);
                    break;
                case JsonObject o:
                    if (o["text"] is JsonValue t && t.TryGetValue<string>(out var text)) sb.Append(text);
                    else if (o["translate"] is JsonValue tr && tr.TryGetValue<string>(out var key)) sb.Append(key);
                    if (o["extra"] is JsonArray extra)
                        foreach (var n in extra) Walk(n, depth + 1);
                    break;
            }
        }

        Walk(component, 0);
        return _FormatCode().Replace(sb.ToString(), "").Replace('\n', ' ').Trim();
    }

    #endregion

    #region 数据包

    /// <summary>列出存档的数据包（<c>.zip</c> 或文件夹，根目录有 <c>pack.mcmeta</c>）。</summary>
    public static List<HorizonPack> ListDataPacks(string worldDir)
    {
        var dir = System.IO.Path.Combine(worldDir, "datapacks");
        if (!Directory.Exists(dir)) return [];
        var (_, disabled) = _ReadDatapacksJson(worldDir);
        var packs = new List<HorizonPack>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var file = System.IO.Path.GetFileName(path);
            if (!Directory.Exists(path) && !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var meta = ReadPackMeta(path);
            if (meta is null) continue;
            packs.Add(new HorizonPack(HorizonPackKind.DataPack, path, file, file, PlainText(meta["pack"]?["description"]),
                !disabled.Contains("file/" + file), null, null));
        }

        return packs.OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>打开或关闭数据包（<c>datapacks.json</c>，保留其他键）。</summary>
    public static void SetDataPackEnabled(string worldDir, HorizonPack pack, bool enabled)
    {
        var id = "file/" + pack.File;
        var (json, _) = _ReadDatapacksJson(worldDir);
        var enabledList = (json["enabled"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        var disabledList = (json["disabled"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        enabledList.Remove(id);
        disabledList.Remove(id);
        if (enabled) enabledList.Add(id);
        else disabledList.Add(id);
        json["enabled"] = new JsonArray(enabledList.Select(s => (JsonNode)s).ToArray());
        json["disabled"] = new JsonArray(disabledList.Select(s => (JsonNode)s).ToArray());
        _WriteDatapacksJson(worldDir, json);
    }

    /// <summary>添加数据包：必须是带 <c>pack.mcmeta</c> 与 <c>data/</c> 的 zip；重名时改为“名称 (n).zip”。</summary>
    public static string AddDataPack(string worldDir, string source)
    {
        HorizonRegistryInstaller.PackFormatFacts(source, requireData: true);
        var dir = System.IO.Path.Combine(worldDir, "datapacks");
        Directory.CreateDirectory(dir);
        var target = FreeName(dir, System.IO.Path.GetFileNameWithoutExtension(source) + ".zip");
        System.IO.File.Copy(source, target);
        _Forget(worldDir, System.IO.Path.GetFileName(target));
        return target;
    }

    /// <summary>删除数据包，并从 datapacks.json 的两个列表中移除（以后同名的包再加进来时是打开的）。</summary>
    public static void RemoveDataPack(string worldDir, HorizonPack pack)
    {
        if (Directory.Exists(pack.Path)) Directory.Delete(pack.Path, true);
        else if (System.IO.File.Exists(pack.Path)) System.IO.File.Delete(pack.Path);
        _Forget(worldDir, pack.File);
    }

    private static void _Forget(string worldDir, string file)
    {
        var id = "file/" + file;
        var (json, _) = _ReadDatapacksJson(worldDir);
        var changed = false;
        foreach (var key in new[] { "enabled", "disabled" })
        {
            if (json[key] is not JsonArray array) continue;
            var kept = array.Select(n => n?.GetValue<string>()).OfType<string>().Where(s => s != id).ToList();
            if (kept.Count == array.Count) continue;
            json[key] = new JsonArray(kept.Select(s => (JsonNode)s).ToArray());
            changed = true;
        }

        if (changed) _WriteDatapacksJson(worldDir, json);
    }

    private static (JsonObject Json, HashSet<string> Disabled) _ReadDatapacksJson(string worldDir)
    {
        JsonObject json;
        try
        {
            var path = System.IO.Path.Combine(worldDir, "datapacks.json");
            json = System.IO.File.Exists(path) ? JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            json = [];
        }

        var disabled = (json["disabled"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet();
        return (json, disabled);
    }

    private static void _WriteDatapacksJson(string worldDir, JsonObject json) =>
        System.IO.File.WriteAllText(System.IO.Path.Combine(worldDir, "datapacks.json"),
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));

    #endregion

    #region 添加与删除

    /// <summary>判断要添加的文件是什么包：<c>.hshader</c> 为光影；zip 根目录有 pack.mcmeta 为资源包，有 shaders/ 为光影。</summary>
    /// <exception cref="InvalidDataException">不是包。</exception>
    public static HorizonPackKind KindOf(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        if (name.EndsWith(".hshader", StringComparison.OrdinalIgnoreCase)) return HorizonPackKind.HorizonShader;
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{name} 不是包：包是 .zip 或 .hshader 文件");
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry("pack.mcmeta") is not null) return HorizonPackKind.ResourcePack;
            if (zip.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith("shaders/", StringComparison.Ordinal)))
                return HorizonPackKind.ShadersFolderPack;
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException($"{name} 不是可读取的 zip 文件");
        }

        throw new InvalidDataException($"{name} 既没有 pack.mcmeta（资源包），也没有 shaders 文件夹（光影包）");
    }

    /// <summary>把包复制进对应文件夹（默认关闭），重名时改为“名称 (n).扩展名”。返回目标路径。</summary>
    public static string AddPack(string gameDir, string source)
    {
        var kind = KindOf(source);
        var dir = System.IO.Path.Combine(gameDir, kind == HorizonPackKind.ResourcePack ? ResourcePacksDir : ShaderPacksDir);
        Directory.CreateDirectory(dir);
        var target = FreeName(dir, System.IO.Path.GetFileName(source));
        System.IO.File.Copy(source, target);
        return target;
    }

    /// <summary>先关掉再删除资源包或光影包。</summary>
    public static void RemovePack(string gameDir, HorizonPack pack)
    {
        if (pack.Enabled)
        {
            if (pack.Kind == HorizonPackKind.ResourcePack) SetResourcePackEnabled(gameDir, pack, false);
            else SetShaderEnabled(gameDir, pack, false);
        }

        if (Directory.Exists(pack.Path)) Directory.Delete(pack.Path, true);
        else if (System.IO.File.Exists(pack.Path)) System.IO.File.Delete(pack.Path);
    }

    public static string FreeName(string dir, string name)
    {
        var target = System.IO.Path.Combine(dir, name);
        if (!System.IO.File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        for (var n = 2;; n++)
        {
            target = System.IO.Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!System.IO.File.Exists(target) && !Directory.Exists(target)) return target;
        }
    }

    #endregion

    [GeneratedRegex("§.")]
    private static partial Regex _FormatCode();
}
