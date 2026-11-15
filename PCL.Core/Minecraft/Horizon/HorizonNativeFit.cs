using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Horizon;

public enum HorizonNativeFitKind
{
    /// <summary>能在所玩的 Horizon 上编译。</summary>
    Fits,

    /// <summary>需要更新的原生钩子版本，所玩的 Horizon 还没有。</summary>
    NeedsNewer,

    /// <summary>为其他 Horizon 提交制作（钉住了别的提交）：可能编译不过，由玩家决定是否尝试。</summary>
    OtherVersion,

    /// <summary>已知在所玩的 Horizon 上编译不过：注册表的 <c>broken_on</c>，或本启动器编译这个文件失败过。构建时跳过。</summary>
    Broken
}

/// <summary>原生模组与所玩 Horizon 的适配情况。</summary>
/// <param name="Code">无法编译时的错误代码（horizon-mixins 的 <c>E_*</c> 或 rustc 的 <c>E0609</c> 等）。</param>
/// <param name="FromRegistry">无法编译的结论来自模组注册表（否则来自本机的编译记录）。</param>
/// <param name="Pin">模组钉住的 Horizon 提交。</param>
/// <param name="Needs">模组需要的原生钩子版本。</param>
/// <param name="Has">所玩 Horizon 的原生钩子版本。</param>
/// <param name="Suggested">注册表中适配所玩 Horizon 的其他版本。</param>
public sealed record HorizonNativeFit(
    HorizonNativeFitKind Kind,
    string? Code = null,
    bool FromRegistry = false,
    string? Pin = null,
    int? Needs = null,
    int? Has = null,
    string? Suggested = null)
{
    public static readonly HorizonNativeFit Fits = new(HorizonNativeFitKind.Fits);
}

/// <summary>本机的一次编译失败：同一个模组文件在同一 Horizon 提交上编译失败过，下次构建前就跳过它。</summary>
public sealed record HorizonNativeFailure(string Id, string Version, string? Sha256, string Horizon, string Code, string Message, string Date);

/// <summary>
///     原生模组在编译前的适配检查，规则与官方启动器的 <c>native_fit</c> 相同（docs/native-mods.md）：
///     <list type="number">
///         <item>注册表记录它在所玩的提交上编译不过（<c>broken_on</c>），或本机编译这个文件失败过：跳过；</item>
///         <item>按原生钩子版本适配的模组：所玩的 Horizon 有这个版本或更新的就能编译；</item>
///         <item>钉住提交的模组：提交相同才算适配，不同的可能编译不过（官方启动器同样要玩家确认“仍然构建”）。</item>
///     </list>
///     horizon-mixins 只检查注入目标是否存在，模组代码里引用的字段或函数在这个提交上不存在时，要到 rustc 类型检查才报错，
///     这时已经编译了几分钟，所以要在编译前用这些记录判断。
/// </summary>
public static partial class HorizonNativeFitCheck
{
    /// <summary>两个提交号是否相同（其中一个可以是至少 7 位的短提交号）。</summary>
    public static bool SameRevision(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (a.Length < 7 || b.Length < 7) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     注册表中对应这个文件的版本：先按文件 SHA-256 找；算不出 SHA-256 时按版本号找。
    ///     文件与注册表的都不同（本地改过或别处来的）时不套用注册表的结论。
    /// </summary>
    public static HorizonRegistryVersion? RegistryVersion(HorizonRegistryIndex? index, string id, string version, string? sha256)
    {
        if (index?.Find(id) is not { } project) return null;
        if (sha256 is not null)
            return project.Versions.FirstOrDefault(v => string.Equals(v.File.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        return project.Versions.FirstOrDefault(v => v.Version == version);
    }

    /// <summary>检查一个原生模组能否在所玩的 Horizon 上编译。</summary>
    /// <param name="manifest">模组清单。</param>
    /// <param name="sha256">模组文件的 SHA-256，用于对照本机的失败记录。</param>
    /// <param name="playedRevision">所玩 Horizon 的提交（可为短提交号）。</param>
    /// <param name="playedHooks">所玩 Horizon 的原生钩子版本，未知时为 null。</param>
    /// <param name="registry">注册表中对应这个文件的版本（见 <see cref="RegistryVersion" />）。</param>
    /// <param name="failures">本机的编译失败记录。</param>
    public static HorizonNativeFit Check(HorizonNativeManifest manifest, string? sha256, string playedRevision, int? playedHooks,
        HorizonRegistryVersion? registry, IReadOnlyCollection<HorizonNativeFailure> failures)
    {
        if (registry?.BrokenOn.FirstOrDefault(b => SameRevision(b.Horizon, playedRevision)) is { } broken)
            return new HorizonNativeFit(HorizonNativeFitKind.Broken, broken.Code, FromRegistry: true, Pin: manifest.Horizon);
        if (FindFailure(failures, manifest.Id, manifest.Version, sha256, playedRevision) is { } failed)
            return new HorizonNativeFit(HorizonNativeFitKind.Broken, failed.Code, Pin: manifest.Horizon);

        if (manifest.Hooks is { } needs)
            return playedHooks is { } has && needs > has
                ? new HorizonNativeFit(HorizonNativeFitKind.NeedsNewer, Needs: needs, Has: has)
                : HorizonNativeFit.Fits;

        return SameRevision(manifest.Horizon, playedRevision)
            ? HorizonNativeFit.Fits
            : new HorizonNativeFit(HorizonNativeFitKind.OtherVersion, Pin: manifest.Horizon);
    }

    /// <summary>
    ///     注册表中能在所玩的 Horizon 上编译的版本（最新的一个，不是 <paramref name="exceptVersion" />）：
    ///     按钩子适配且钩子版本够，或钉住的正是所玩的提交；没被撤回，也没记录在所玩的提交上编译不过。
    /// </summary>
    public static HorizonRegistryVersion? FittingVersion(HorizonRegistryIndex? index, string id, string playedRevision, int? playedHooks,
        string? exceptVersion = null)
    {
        if (index?.Find(id) is not { } project) return null;
        return project.Versions
            .Where(v => v.Version != exceptVersion && v.Game == HorizonRegistry.Game)
            .Where(v => index.FindRevoked(id, v.File.Sha256) is null)
            .Where(v => !v.BrokenOn.Any(b => SameRevision(b.Horizon, playedRevision)))
            .FirstOrDefault(v => v.Hooks is { } hooks
                ? playedHooks is { } has && hooks <= has
                : SameRevision(v.NativePin, playedRevision));
    }

    #region 本机编译失败记录

    public const string FailuresFileName = "native-failures.json";

    public static HorizonNativeFailure? FindFailure(IEnumerable<HorizonNativeFailure> failures, string id, string version, string? sha256,
        string revision) =>
        failures.FirstOrDefault(f => f.Id == id && SameRevision(f.Horizon, revision) &&
                                     (f.Sha256 is not null && sha256 is not null
                                         ? string.Equals(f.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                                         : f.Version == version));

    /// <summary>读取失败记录；文件不存在或损坏时返回空列表。</summary>
    public static List<HorizonNativeFailure> ReadFailures(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonArray array) return [];
            return array.OfType<JsonObject>()
                .Select(o => new HorizonNativeFailure(
                    o["id"]?.GetValue<string>() ?? "",
                    o["version"]?.GetValue<string>() ?? "",
                    o["sha256"]?.GetValue<string>(),
                    o["horizon"]?.GetValue<string>() ?? "",
                    o["code"]?.GetValue<string>() ?? "",
                    o["message"]?.GetValue<string>() ?? "",
                    o["date"]?.GetValue<string>() ?? ""))
                .Where(f => f.Id.Length > 0 && f.Horizon.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>记下一次失败（同一模组文件在同一提交上的旧记录被替换）。</summary>
    public static void RecordFailure(string file, HorizonNativeFailure failure)
    {
        var failures = ReadFailures(file);
        failures.RemoveAll(f => f.Id == failure.Id && SameRevision(f.Horizon, failure.Horizon) &&
                                (f.Sha256 is not null && failure.Sha256 is not null
                                    ? string.Equals(f.Sha256, failure.Sha256, StringComparison.OrdinalIgnoreCase)
                                    : f.Version == failure.Version));
        failures.Add(failure);
        var array = new JsonArray();
        foreach (var f in failures)
            array.Add(new JsonObject
            {
                ["id"] = f.Id, ["version"] = f.Version, ["sha256"] = f.Sha256, ["horizon"] = f.Horizon,
                ["code"] = f.Code, ["message"] = f.Message, ["date"] = f.Date
            });
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    #endregion

    #region 编译错误归因

    [GeneratedRegex(@"error\[(E\d{4})\]")]
    private static partial Regex ErrorCode();

    [GeneratedRegex(@"^\s*\d+\s*\|\s?(.*)$")]
    private static partial Regex SnippetLine();

    /// <summary>
    ///     找出 rustc 报错的代码来自哪个模组：rustc 的报错会附上出错的源代码行，模组的模块与注入代码几乎原样插进 Horizon 的源文件，
    ///     所以拿这些行去模组的 <c>.rs</c> 文件里找，命中最多的模组就是原因。找不到或无法区分时返回 null。
    /// </summary>
    /// <param name="renderedErrors">rustc 报错的完整文本（<c>--message-format=json</c> 的 <c>rendered</c>）。</param>
    /// <param name="bundles">参与编译的模组：id 与 <c>mixins/&lt;id&gt;/</c> 下的文件。</param>
    public static (string Id, string Code)? FindCompileCulprit(IEnumerable<string> renderedErrors,
        IEnumerable<(string Id, IReadOnlyDictionary<string, byte[]> Files)> bundles)
    {
        var sources = bundles
            .Select(b => (b.Id, Lines: b.Files
                .Where(f => f.Key.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
                .SelectMany(f => Encoding.UTF8.GetString(f.Value).Split('\n'))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.Ordinal)))
            .ToList();
        if (sources.Count == 0) return null;

        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        string? code = null;
        foreach (var error in renderedErrors)
        {
            var errorCode = ErrorCode().Match(error) is { Success: true } m ? m.Groups[1].Value : null;
            var hit = false;
            foreach (var raw in error.Replace("\r\n", "\n").Split('\n'))
            {
                var snippet = SnippetLine().Match(raw);
                if (!snippet.Success) continue;
                var line = snippet.Groups[1].Value.Trim();
                // 只拿像代码的行：太短的（如“}”）与只有 ^ - 标记的行没有区分度
                if (line.Length < 8 || !line.Any(char.IsLetter) || line.All(c => c is '^' or '-' or ' ' or '|')) continue;
                foreach (var (id, lines) in sources)
                    if (lines.Contains(line))
                    {
                        scores[id] = scores.GetValueOrDefault(id) + 1;
                        hit = true;
                    }
            }

            if (hit) code ??= errorCode;
        }

        if (scores.Count == 0) return null;
        var best = scores.Values.Max();
        var top = scores.Where(s => s.Value == best).Select(s => s.Key).ToList();
        return top.Count == 1 ? (top[0], code ?? "build") : null;
    }

    #endregion
}
