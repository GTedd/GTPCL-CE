using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     游戏目录里的一个模组（文件或文件夹）。读不出来的模组 <see cref="Info" /> 为 null，<see cref="Error" /> 为原因。
/// </summary>
public sealed record HorizonInstalledMod(
    string Path,
    bool InNativeFolder,
    HorizonModInfo? Info,
    string? Error,
    bool Enabled)
{
    /// <summary>用于启用表的 id：读得出时用清单里的，否则用文件名。</summary>
    public string Id => Info?.Id ?? System.IO.Path.GetFileNameWithoutExtension(Path.TrimEnd('\\', '/'));
}

/// <summary>
///     Horizon 游戏目录（<c>--game-dir</c>）里的模组：位置、开关与安装，遵循 Horizon 的 Content API（docs/api/content.md）。
///     <list type="bullet">
///         <item>内容模组：<c>horizon-mods/&lt;id&gt;.hmod</c> 或含 <c>horizon-mod.toml</c> 的文件夹。</item>
///         <item>原生模组：<c>horizon-native-mods/&lt;id&gt;.hnmod</c>（或带原生代码的 <c>.hmod</c>），编译进游戏后才运行。</item>
///         <item>开关都在 <c>horizon-mods.txt</c>：<c>mod.&lt;id&gt;:true</c> 为开；关闭时删掉该行，其余行原样保留。</item>
///     </list>
///     这些文件只应在游戏未运行时修改：游戏启动时读取它们。
/// </summary>
public static class HorizonGameContent
{
    public const string ContentModsDir = "horizon-mods";
    public const string NativeModsDir = "horizon-native-mods";
    public const string ModsListFile = "horizon-mods.txt";

    private static readonly object _ListLock = new();

    public static string ContentModsPath(string gameDir) => Path.Combine(gameDir, ContentModsDir);
    public static string NativeModsPath(string gameDir) => Path.Combine(gameDir, NativeModsDir);

    /// <summary><c>horizon-mods.txt</c> 中为 true 的 id。</summary>
    public static HashSet<string> ReadEnabled(string gameDir)
    {
        var path = Path.Combine(gameDir, ModsListFile);
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return enabled;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            if (!key.StartsWith("mod.", StringComparison.Ordinal)) continue;
            if (line[(colon + 1)..].Trim() == "true") enabled.Add(key[4..]);
        }

        return enabled;
    }

    /// <summary>打开或关闭模组 <paramref name="id" />，保留其余行（与官方启动器、游戏 Options &gt; Mods 一致）。</summary>
    public static void SetEnabled(string gameDir, string id, bool enabled)
    {
        lock (_ListLock)
        {
            var path = Path.Combine(gameDir, ModsListFile);
            var key = "mod." + id;
            var lines = File.Exists(path)
                ? File.ReadAllLines(path, Encoding.UTF8)
                    .Where(line =>
                    {
                        var colon = line.IndexOf(':');
                        return colon < 0 || line[..colon].Trim() != key;
                    })
                    .ToList()
                : [];
            if (enabled) lines.Add(key + ":true");
            Directory.CreateDirectory(gameDir);
            var text = string.Join('\n', lines);
            if (text.Length > 0) text += "\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    /// <summary>列出游戏目录里的内容模组与原生模组。</summary>
    public static List<HorizonInstalledMod> List(string gameDir)
    {
        var enabled = ReadEnabled(gameDir);
        var result = new List<HorizonInstalledMod>();

        var contentDir = ContentModsPath(gameDir);
        if (Directory.Exists(contentDir))
        {
            foreach (var file in Directory.EnumerateFiles(contentDir, "*.hmod"))
                result.Add(_Describe(file, false, enabled));
            foreach (var folder in Directory.EnumerateDirectories(contentDir))
            {
                // .horizon-mod-data 等隐藏文件夹存的是模组数据，不是模组
                if (Path.GetFileName(folder).StartsWith('.')) continue;
                if (File.Exists(Path.Combine(folder, HorizonModFile.ContentManifest)))
                    result.Add(_Describe(folder, false, enabled));
            }
        }

        var nativeDir = NativeModsPath(gameDir);
        if (Directory.Exists(nativeDir))
        {
            foreach (var file in Directory.EnumerateFiles(nativeDir))
            {
                if (file.EndsWith(".hnmod", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".hmod", StringComparison.OrdinalIgnoreCase))
                    result.Add(_Describe(file, true, enabled));
            }
        }

        return result
            .OrderBy(m => m.Info?.Name ?? m.Id, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static HorizonInstalledMod _Describe(string path, bool inNativeFolder, HashSet<string> enabled)
    {
        HorizonModFile.TryRead(path, out var info, out var error);
        if (info is not null && inNativeFolder && !info.HasNativeCode)
        {
            error = "原生模组文件夹里的这个文件不含原生代码，请把它放到 horizon-mods 文件夹";
        }
        else if (info is not null && !inNativeFolder && info.Kind == HorizonModKind.Native)
        {
            error = "这是原生模组，请放到 horizon-native-mods 文件夹，由启动器编译进游戏";
        }

        var id = info?.Id ?? Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        return new HorizonInstalledMod(path, inNativeFolder, info, error, enabled.Contains(id));
    }

    /// <summary>
    ///     安装一个模组文件或文件夹：校验后复制为 <c>&lt;id&gt;.hmod</c> / <c>&lt;id&gt;.hnmod</c>（文件夹按 id 命名）。
    ///     带原生代码的进 <c>horizon-native-mods/</c>，其余进 <c>horizon-mods/</c>。新装的模组默认打开。
    /// </summary>
    /// <param name="replace">同 id 的模组已存在时是否替换；为 false 时抛出 <see cref="IOException" />。</param>
    /// <exception cref="InvalidDataException">不是合法的 Horizon 模组。</exception>
    public static HorizonModInfo Install(string gameDir, string source, bool replace, bool enable = true)
    {
        var info = HorizonModFile.Read(source);
        var native = info.HasNativeCode;
        var targetDir = native ? NativeModsPath(gameDir) : ContentModsPath(gameDir);
        Directory.CreateDirectory(targetDir);

        var extension = info.Kind == HorizonModKind.Native ? ".hnmod" : ".hmod";
        var target = info.IsFolder ? Path.Combine(targetDir, info.Id) : Path.Combine(targetDir, info.Id + extension);

        // 同一个 id 可能以别的文件名存在（手动放入的），一并视为已存在
        var existing = List(gameDir).Where(m => m.Id == info.Id && !_SamePath(m.Path, source)).ToList();
        if (existing.Count > 0 && !replace)
            throw new IOException($"已经安装了 id 为 {info.Id} 的模组");
        foreach (var old in existing) _DeletePath(old.Path);

        if (!_SamePath(source, target))
        {
            if (info.IsFolder) _CopyDirectory(source, target);
            else File.Copy(source, target, true);
        }

        if (enable) SetEnabled(gameDir, info.Id, true);
        return info with { Path = target };
    }

    /// <summary>删除模组文件（或文件夹）、它在启用表里的行，以及原生模组旁的来源记录 <c>&lt;id&gt;.origin.json</c>。</summary>
    public static void Remove(string gameDir, HorizonInstalledMod mod)
    {
        _DeletePath(mod.Path);
        if (mod.InNativeFolder)
        {
            var origin = Path.Combine(NativeModsPath(gameDir), mod.Id + ".origin.json");
            if (File.Exists(origin)) File.Delete(origin);
        }

        // 同 id 的其他副本还在时保留开关
        if (List(gameDir).All(m => m.Id != mod.Id)) SetEnabled(gameDir, mod.Id, false);
    }

    private static void _DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static bool _SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static void _CopyDirectory(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, true);
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }
}
