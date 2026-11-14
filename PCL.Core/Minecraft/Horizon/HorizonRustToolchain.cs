using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>一个要下载的工具链组件。</summary>
public sealed record HorizonToolchainComponent(string Name, IReadOnlyList<string> Urls, string Sha256, string FileName);

/// <summary>
///     原生模组编译用的 Rust 工具链（Windows x64），与官方启动器固定的版本、来源和 SHA-256 完全一致
///     （launcher/src/rust_toolchain.rs）：Rust 1.99.1 的 rustc、cargo、rust-std、rust-mingw 与 WinLibs 的 GCC。
///     不使用 rustup，也不碰玩家自己的 Rust。
/// </summary>
public static partial class HorizonRustToolchain
{
    public const string Version = "1.99.1";
    public const string Target = "x86_64-pc-windows-gnu";
    private const string Dist = "https://static.rust-lang.org/dist/2026-10-15";

    /// <summary>
    ///     下载来源：国内镜像在前（实测 rsproxy 约 2.6 MB/s，官方源约 40 KB/s），官方源兜底。
    ///     文件由固定的 SHA-256 校验，镜像无法篡改。
    /// </summary>
    private static readonly string[] _DistMirrors =
    [
        "https://rsproxy.cn/dist/2026-10-15",
        Dist,
        "https://mirrors.ustc.edu.cn/rust-static/dist/2026-10-15"
    ];

    /// <summary>
    ///     Cargo 的 crates.io 镜像（sparse 索引的源替换）。构建都带 <c>--locked</c>，每个 crate 都按 Cargo.lock 中的校验和检查，
    ///     镜像无法篡改；镜像下载失败时改回直连 crates.io 重试。
    /// </summary>
    public static IReadOnlyList<string> CratesMirrorConfig { get; } =
    [
        "source.crates-io.replace-with='pcl-mirror'",
        "source.pcl-mirror.registry='sparse+https://rsproxy.cn/index/'"
    ];

    private static readonly (string Name, string Sha256)[] _RustComponents =
    [
        ("rustc", "43b17e9a9134ea72562bfbb23728a830f671ea9be1db6a72597a8d240e986b35"),
        ("cargo", "57e54617be4420dbb967ca8293f5ace7168104ac12efa9dc7c5c3bda112f8b36"),
        ("rust-std", "6546651238a3ac092b763511be524ca6eda6103e5d47342416923be1b3ce837c"),
        ("rust-mingw", "30263d7bb4fd0bf63a153afe36d7fc102f929a7d42781651c6b3a03a414ba428")
    ];

    private const string WinLibsUrl =
        "https://github.com/brechtsanders/winlibs_mingw/releases/download/16.2.0posix-23.1.3-14.0.0-msvcrt-r3/winlibs-x86_64-posix-seh-gcc-16.2.0-mingw-w64msvcrt-14.0.0-r3.zip";

    private const string WinLibsSha256 = "62af32ea58acdea1318ea5dd8eb7017eaee694dd40b269d52a59c769dcfefb82";

    /// <summary>
    ///     Smart App Control 拦截编译时的说明（官方启动器原文的中文版）。
    /// </summary>
    public const string SmartAppControlMessage =
        "Windows 的“智能应用控制”（Smart App Control）阻止了编译（os error 4551）。它只允许已签名的程序运行，" +
        "而原生构建要运行本机刚编译出来的程序和编译器自带的部分程序，它们都没有签名。要编译原生模组，请在" +
        "“Windows 安全中心 > 应用和浏览器控制 > 智能应用控制设置”中关闭它后重新构建。部分 Windows 版本关闭后无法再打开，" +
        "除非重装系统。内容模组（.hmod）不需要编译，开着它也能使用。";

    /// <summary>编译过程会删掉的环境变量（会改变编译参数或目标），<c>CARGO_BUILD_JOBS</c> 除外。</summary>
    private static readonly string[] _RemovedVariables =
    [
        "RUSTUP_TOOLCHAIN", "RUSTC_WRAPPER", "RUSTC_WORKSPACE_WRAPPER", "RUSTFLAGS", "CARGO_ENCODED_RUSTFLAGS",
        "RUSTDOCFLAGS", "CARGO_ENCODED_RUSTDOCFLAGS", "CARGO_INCREMENTAL"
    ];

    private static readonly string[] _RemovedPrefixes = ["CARGO_PROFILE_", "CARGO_BUILD_", "CARGO_TARGET_"];

    public static string ToolchainDir(string buildRoot) => Path.Combine(buildRoot, "rust", $"{Version}-{Target}");
    public static string CargoHome(string buildRoot) => Path.Combine(buildRoot, "cargo");
    public static string TempDir(string buildRoot) => Path.Combine(buildRoot, "tmp");
    public static string CargoExe(string buildRoot) => Path.Combine(ToolchainDir(buildRoot), "bin", "cargo.exe");
    public static string RustcExe(string buildRoot) => Path.Combine(ToolchainDir(buildRoot), "bin", "rustc.exe");

    /// <summary>工具链已完整安装：有 <c>.installed</c> 与 <c>mingw64\bin\gcc.exe</c>。</summary>
    public static bool IsInstalled(string buildRoot)
    {
        var dir = ToolchainDir(buildRoot);
        return File.Exists(Path.Combine(dir, ".installed")) && File.Exists(Path.Combine(dir, "mingw64", "bin", "gcc.exe"));
    }

    /// <summary>要下载的组件；WinLibs 在 GitHub 上，可经 <paramref name="githubProxies" /> 加速（有 SHA-256 校验）。</summary>
    public static IReadOnlyList<HorizonToolchainComponent> Components(IReadOnlyList<string> githubProxies)
    {
        var list = _RustComponents
            .Select(c =>
            {
                var file = $"{c.Name}-{Version}-{Target}.tar.gz";
                return new HorizonToolchainComponent(c.Name, _DistMirrors.Select(m => $"{m}/{file}").ToList(), c.Sha256, file);
            })
            .ToList();
        list.Add(new HorizonToolchainComponent("mingw64",
            githubProxies.Select(p => p.TrimEnd('/') + "/" + WinLibsUrl).Append(WinLibsUrl).ToList(),
            WinLibsSha256, "mingw64.zip"));
        return list;
    }

    /// <summary>
    ///     解压一个 Rust 组件（.tar.gz）：去掉前两级目录（<c>&lt;名称&gt;-1.99.1-&lt;目标&gt;/&lt;组件&gt;/</c>），
    ///     跳过 <c>manifest.in</c>、链接与越界路径。
    /// </summary>
    public static void ExtractRustComponent(string archive, string destination)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                continue;
            var parts = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p is "." or "..") || entry.Name.StartsWith('/') || parts.Length <= 2) continue;
            var rest = parts[2..];
            if (rest is ["manifest.in"]) continue;
            var target = Path.Combine([destination, .. rest]);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    /// <summary>解压 WinLibs：只取 <c>mingw64/&lt;x&gt;/…</c>（<c>x</c> 不是 share）。</summary>
    public static void ExtractWinLibs(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "mingw64" || parts[1] == "share" || parts.Any(p => p is "." or "..")) continue;
            var target = Path.Combine([destination, .. parts]);
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    /// <summary>
    ///     编译用的工作目录：工具只能可靠处理纯 ASCII、无空格的路径（MinGW 打不开中文路径，rustc 会把 8.3 短名还原成长名）。
    ///     依次尝试候选目录，都不合适时用 <c>%ProgramData%\pcl-horizon-&lt;哈希&gt;</c>。
    /// </summary>
    public static string ChooseBuildRoot(IEnumerable<string> candidates, string keySource)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Path.IsPathRooted(programData) || !IsToolSafe(programData)) programData = @"C:\ProgramData";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keySource)).AsSpan(0, 4));
        var fallback = Path.Combine(programData, "pcl-horizon-" + key);
        var all = candidates.Where(IsToolSafe).Select(Path.GetFullPath).Append(fallback).ToList();

        // 已经装过工具链的目录优先（不必重新下载）；其余目录要有足够的剩余空间，免得把磁盘写满
        foreach (var candidate in all.Where(c => Directory.Exists(Path.Combine(c, "rust"))))
            return candidate;
        foreach (var candidate in all)
        {
            if (FreeSpace(candidate) is { } free && free < FirstBuildSpace) continue;
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 试下一个
            }
        }

        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>首次构建大约需要的磁盘空间：实测工具链 1.7 GB、依赖 0.6 GB、编译产物约 4.8 GB，另留余量。</summary>
    public const long FirstBuildSpace = 10L << 30;

    /// <summary>之后的增量构建至少应留的空间。</summary>
    public const long RebuildSpace = 3L << 30;

    /// <summary>路径所在磁盘的剩余空间；无法判断时返回 null。</summary>
    public static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>路径全为 ASCII 且不含空格。</summary>
    public static bool IsToolSafe(string path) => path.All(c => c < 128 && c != ' ');

    /// <summary>
    ///     创建 cargo / rustc / horizon-mixins 进程：工具链的 PATH、RUSTC、CARGO_HOME 与 TMP，删掉会改变编译参数的变量，
    ///     从磁盘根目录启动（Cargo 会读取启动目录及其上级的 .cargo/config.toml，不能从用户目录启动）。
    /// </summary>
    public static ProcessStartInfo StartInfo(string buildRoot, string program, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null, string? workingDirectory = null)
    {
        var toolchain = ToolchainDir(buildRoot);
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory ?? Path.GetPathRoot(Path.GetFullPath(buildRoot))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        foreach (var name in info.Environment.Keys.ToList())
        {
            var upper = name.ToUpperInvariant();
            if (_RemovedVariables.Contains(upper) ||
                _RemovedPrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal)) && upper != "CARGO_BUILD_JOBS")
                info.Environment.Remove(name);
        }

        info.Environment["PATH"] = string.Join(Path.PathSeparator,
            Path.Combine(toolchain, "bin"), Path.Combine(toolchain, "mingw64", "bin"),
            Environment.GetEnvironmentVariable("PATH") ?? "");
        info.Environment["RUSTC"] = RustcExe(buildRoot);
        info.Environment["CARGO_HOME"] = CargoHome(buildRoot);
        var temp = TempDir(buildRoot);
        try
        {
            Directory.CreateDirectory(temp);
            info.Environment["TMP"] = temp;
            info.Environment["TEMP"] = temp;
            info.Environment["TMPDIR"] = temp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保持原来的临时目录
        }

        if (extraEnvironment is not null)
            foreach (var (key, value) in extraEnvironment)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
            }

        return info;
    }

    /// <summary>cargo 进度：<c>Compiling x v…</c> 计数，<c>Building [...] done/total</c> 给出总数。</summary>
    public sealed class CargoProgress
    {
        public int Crates { get; private set; }
        public int Total { get; private set; }
        public string? Current { get; private set; }

        public double? Fraction => Total > 0 ? Math.Clamp((double)Crates / Total, 0, 1) : null;

        /// <summary>处理 cargo 的一行标准错误输出；返回去掉进度条后的可记录文本（可能为空）。</summary>
        public string Feed(string line)
        {
            var text = _AnsiEscape().Replace(line, "");
            var kept = new List<string>();
            foreach (var piece in text.Split('\r'))
            {
                var trimmed = piece.Trim();
                var building = _BuildingBar().Match(trimmed);
                if (building.Success)
                {
                    Crates = Math.Max(Crates, int.Parse(building.Groups[1].Value));
                    Total = int.Parse(building.Groups[2].Value);
                    continue;
                }

                if (trimmed.StartsWith("Compiling ", StringComparison.Ordinal))
                {
                    Crates++;
                    Current = trimmed["Compiling ".Length..].Split(' ')[0];
                }

                if (trimmed.Length > 0) kept.Add(trimmed);
            }

            return string.Join('\n', kept);
        }
    }

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex _AnsiEscape();

    [GeneratedRegex(@"^Building \[.*?\]\s+(\d+)/(\d+)")]
    private static partial Regex _BuildingBar();
}
