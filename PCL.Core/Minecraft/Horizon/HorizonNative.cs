using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     为编译读出的原生模组包（<c>.hnmod</c> 或带原生代码的 <c>.hmod</c>）：清单与 <c>mixins/&lt;id&gt;/</c> 下的全部文件。
/// </summary>
public sealed partial class HorizonNativeBundle
{
    private static readonly HashSet<string> _NonMixinKeys = ["mod", "compatibility", "api", "dependencies"];

    public required string Path { get; init; }
    public required HorizonNativeManifest Manifest { get; init; }

    /// <summary><c>mixins/&lt;id&gt;/</c> 下的文件，键为相对该目录、以 <c>/</c> 分隔的路径。</summary>
    public required SortedDictionary<string, byte[]> Files { get; init; }

    /// <summary>
    ///     源文件摘要：按压缩包内路径排序，对每个原生文件与内容文件依次哈希“路径、u64 LE 长度、内容”，用于判断是否需要重新编译。
    /// </summary>
    public required string SourceDigest { get; init; }

    public string Id => Manifest.Id;
    public string Version => Manifest.Version;

    public string MixinToml => Encoding.UTF8.GetString(Files[HorizonModFile.MixinManifest]);

    /// <summary>horizon-mixin.toml 中除 mod、compatibility、api、dependencies 外还有其他表（[[injection]]、[[module]] 等）。</summary>
    public bool HasMixinsContent => HorizonToml.Parse(MixinToml).Keys.Any(k => !_NonMixinKeys.Contains(k));

    /// <summary>使用 <c>[api]</c>（trait 模组），需要额外生成 crate，暂不支持。</summary>
    public bool UsesApi => HorizonToml.Parse(MixinToml).ContainsKey("api");

    /// <summary>读取并校验（与 <see cref="HorizonModFile.Read" /> 规则相同）。</summary>
    /// <exception cref="InvalidDataException">不是合法的原生模组。</exception>
    public static HorizonNativeBundle Read(string path)
    {
        var info = HorizonModFile.Read(path);
        if (info.Native is null) throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} 不含原生代码");

        var prefix = $"mixins/{info.Id}/";
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var source = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            source[entry.FullName] = bytes;
            if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                files[entry.FullName[prefix.Length..]] = bytes;
        }

        return new HorizonNativeBundle
        {
            Path = path,
            Manifest = info.Native,
            Files = files,
            SourceDigest = Convert.ToHexStringLower(HashFiles(source))
        };
    }

    /// <summary>把 <c>mixins/&lt;id&gt;/</c> 的内容写到 <paramref name="folder" />（编译用的副本）。</summary>
    public void ExtractTo(string folder, string? mixinTomlOverride = null)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in Files)
        {
            var target = System.IO.Path.Combine(folder, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (name == HorizonModFile.MixinManifest && mixinTomlOverride is not null)
                File.WriteAllText(target, mixinTomlOverride, new UTF8Encoding(false));
            else
                File.WriteAllBytes(target, bytes);
        }
    }

    /// <summary>
    ///     把 <c>[compatibility] horizon_revision</c> 改成 <paramref name="revision" />（让模组在实例所玩的提交上编译）。
    /// </summary>
    public static string Repin(string toml, string revision)
    {
        var lines = toml.Replace("\r\n", "\n").Split('\n').ToList();
        var inCompatibility = false;
        var compatibilityLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                inCompatibility = trimmed == "[compatibility]";
                if (inCompatibility) compatibilityLine = i;
                continue;
            }

            if (inCompatibility && _HorizonRevisionLine().IsMatch(trimmed))
            {
                lines[i] = $"horizon_revision = \"{revision}\"";
                return string.Join('\n', lines);
            }
        }

        if (compatibilityLine >= 0) lines.Insert(compatibilityLine + 1, $"horizon_revision = \"{revision}\"");
        else lines.AddRange(["", "[compatibility]", $"horizon_revision = \"{revision}\""]);
        return string.Join('\n', lines);
    }

    /// <summary>对文件集合依次哈希“名称、u64 LE 长度、内容”。</summary>
    public static byte[] HashFiles(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (name, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(name));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)bytes.Length);
            sha.AppendData(length);
            sha.AppendData(bytes);
        }

        return sha.GetHashAndReset();
    }

    [GeneratedRegex(@"^horizon_revision\s*=")]
    private static partial Regex _HorizonRevisionLine();
}

/// <summary>编译进构建的一个模组（<c>native-mods.json</c> 的 <c>mods</c>）。</summary>
public sealed record HorizonNativeBuiltMod(string Id, string Version, string? Sha256);

/// <summary>没编进构建的模组及原因（<c>left_out</c>）。<c>Why</c>：other-version、clash、needs，本启动器另有 unsupported。</summary>
public sealed record HorizonNativeLeftOut(string Id, string Version, string Why, string? With);

/// <summary>
///     原生构建的记录 <c>native-mods.json</c>，字段与官方启动器相同（docs/native-mods.md）。
/// </summary>
public sealed record HorizonNativeRecord
{
    public required string Base { get; init; }
    public required string Horizon { get; init; }
    public string? BaseHorizon { get; init; }
    public IReadOnlyList<string> PinnedBy { get; init; } = [];
    public bool OnBase { get; init; } = true;
    public required string HorizonMixins { get; init; }
    public int? Hooks { get; init; }
    public string Rust { get; init; } = HorizonRustToolchain.Version;
    public string Profile { get; init; } = "performance";
    public string Target { get; init; } = "client";
    public string? RustTarget { get; init; } = HorizonRustToolchain.Target;
    public IReadOnlyList<HorizonNativeBuiltMod> Mods { get; init; } = [];
    public IReadOnlyList<HorizonNativeLeftOut> LeftOut { get; init; } = [];

    public const string FileName = "native-mods.json";

    public string ToJson()
    {
        // 与官方一致：紧凑 JSON，键按字母排序
        var json = new JsonObject
        {
            ["base"] = Base,
            ["base_horizon"] = BaseHorizon,
            ["hooks"] = Hooks,
            ["horizon"] = Horizon,
            ["horizon_mixins"] = HorizonMixins,
            ["left_out"] = new JsonArray(LeftOut.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["version"] = l.Version, ["why"] = l.Why, ["with"] = l.With
            }).ToArray()),
            ["mods"] = new JsonArray(Mods.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["sha256"] = m.Sha256, ["version"] = m.Version
            }).ToArray()),
            ["on_base"] = OnBase,
            ["pinned_by"] = new JsonArray(PinnedBy.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
            ["profile"] = Profile,
            ["rust"] = Rust,
            ["rust_target"] = RustTarget,
            ["target"] = Target
        };
        return json.ToJsonString();
    }

    /// <returns>读不到或格式不对时返回 null。</returns>
    public static HorizonNativeRecord? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            var horizon = o["horizon"]?.GetValue<string>();
            if (horizon is null || horizon.Length != 40) return null;
            return new HorizonNativeRecord
            {
                Base = o["base"]?.GetValue<string>() ?? "",
                Horizon = horizon,
                BaseHorizon = o["base_horizon"]?.GetValue<string>(),
                PinnedBy = (o["pinned_by"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList(),
                OnBase = o["on_base"]?.GetValue<bool>() ?? false,
                HorizonMixins = o["horizon_mixins"]?.GetValue<string>() ?? "",
                Hooks = o["hooks"]?.GetValue<int?>(),
                Rust = o["rust"]?.GetValue<string>() ?? "",
                Profile = o["profile"]?.GetValue<string>() ?? "performance",
                Target = o["target"]?.GetValue<string>() ?? "client",
                RustTarget = o["rust_target"]?.GetValue<string>(),
                Mods = (o["mods"] as JsonArray ?? []).OfType<JsonObject>()
                    .Where(m => m["id"] is not null && m["version"] is not null)
                    .Select(m => new HorizonNativeBuiltMod(m["id"]!.GetValue<string>(), m["version"]!.GetValue<string>(),
                        m["sha256"]?.GetValue<string>()))
                    .ToList(),
                LeftOut = (o["left_out"] as JsonArray ?? []).OfType<JsonObject>()
                    .Where(m => m["id"] is not null && m["version"] is not null)
                    .Select(m => new HorizonNativeLeftOut(m["id"]!.GetValue<string>(), m["version"]!.GetValue<string>(),
                        m["why"]?.GetValue<string>() ?? "", m["with"]?.GetValue<string>()))
                    .ToList()
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>原生模组相对实例所玩构建的状态。</summary>
public enum HorizonNativeState
{
    /// <summary>没有打开的原生模组，不需要构建。</summary>
    NoMods,

    /// <summary>有打开的原生模组，但实例玩的不是原生构建。</summary>
    NotBuilt,

    /// <summary>实例玩的原生构建与打开的模组不一致。</summary>
    NeedsRebuild,

    /// <summary>实例玩的原生构建正好由这些模组编译。</summary>
    UpToDate
}

/// <summary>原生构建的纯逻辑部分：状态比较、构建键、暂存目录同步。</summary>
public static class HorizonNativeLogic
{
    /// <summary>
    ///     比较打开的模组与构建记录（mods ∪ left_out）：id、版本一致且摘要一致（任一方没有摘要时忽略）即为最新。
    /// </summary>
    public static HorizonNativeState State(IReadOnlyCollection<HorizonNativeBuiltMod> enabled, HorizonNativeRecord? built)
    {
        if (enabled.Count == 0) return HorizonNativeState.NoMods;
        if (built is null) return HorizonNativeState.NotBuilt;

        var a = enabled.OrderBy(m => m.Id, StringComparer.Ordinal).ThenBy(m => m.Version, StringComparer.Ordinal)
            .DistinctBy(m => (m.Id, m.Version)).ToList();
        var b = built.Mods.Concat(built.LeftOut.Select(l => new HorizonNativeBuiltMod(l.Id, l.Version, null)))
            .OrderBy(m => m.Id, StringComparer.Ordinal).ThenBy(m => m.Version, StringComparer.Ordinal)
            .DistinctBy(m => (m.Id, m.Version)).ToList();
        if (a.Count != b.Count) return HorizonNativeState.NeedsRebuild;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Version != b[i].Version) return HorizonNativeState.NeedsRebuild;
            if (a[i].Sha256 is not null && b[i].Sha256 is not null &&
                !string.Equals(a[i].Sha256, b[i].Sha256, StringComparison.OrdinalIgnoreCase))
                return HorizonNativeState.NeedsRebuild;
        }

        return HorizonNativeState.UpToDate;
    }

    /// <summary>
    ///     构建版本名 <c>native-&lt;提交前 7 位&gt;-&lt;键前 6 字节&gt;</c>。键覆盖 horizon-mixins 提交、编译配置、Rust 版本、
    ///     Horizon 提交以及每个模组副本的全部文件，同样的组合不会重复编译。
    /// </summary>
    public static string VersionName(
        string mixinsRevision,
        string cargoProfile,
        IReadOnlyList<string> extraConfig,
        string revision,
        IEnumerable<(string Id, IReadOnlyDictionary<string, byte[]> Files)> bundles)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Part(string s)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(s));
            sha.AppendData([0]);
        }

        Part(mixinsRevision);
        Part(cargoProfile);
        foreach (var config in extraConfig) Part(config);
        Part("horizon-client/mods");
        Part(HorizonRustToolchain.Version);
        Part(revision);
        Span<byte> length = stackalloc byte[8];
        foreach (var (id, files) in bundles)
        {
            Part(id);
            foreach (var (name, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                sha.AppendData(Encoding.UTF8.GetBytes(name));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)bytes.Length);
                sha.AppendData(length);
                sha.AppendData(bytes);
            }
        }

        var key = Convert.ToHexStringLower(sha.GetHashAndReset().AsSpan(0, 6));
        return $"native-{revision[..7]}-{key}";
    }

    /// <summary>
    ///     把 <paramref name="from" /> 同步到 <paramref name="to" />：只写内容变化或缺失的文件，并把写入的文件时间设为现在
    ///     （Cargo 按文件时间判断要不要重编，复制保留旧时间会让它误判为已编译），删除 <paramref name="to" /> 中多余的文件。
    /// </summary>
    /// <returns>写入的文件数。</returns>
    public static int Sync(string from, string to)
    {
        Directory.CreateDirectory(to);
        var written = 0;
        var fromFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"暂存目录里不能有链接：{file}");
            var relative = System.IO.Path.GetRelativePath(from, file);
            fromFiles.Add(relative);
            var target = System.IO.Path.Combine(to, relative);
            if (File.Exists(target) && _SameContent(file, target)) continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            written++;
        }

        foreach (var file in Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories).ToList())
        {
            if (!fromFiles.Contains(System.IO.Path.GetRelativePath(to, file))) File.Delete(file);
        }

        return written;
    }

    private static bool _SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        const int size = 64 * 1024;
        var ba = new byte[size];
        var bb = new byte[size];
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        while (true)
        {
            var ra = sa.ReadAtLeast(ba, size, false);
            var rb = sb.ReadAtLeast(bb, size, false);
            if (ra != rb || !ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
            if (ra < size) return true;
        }
    }
}
