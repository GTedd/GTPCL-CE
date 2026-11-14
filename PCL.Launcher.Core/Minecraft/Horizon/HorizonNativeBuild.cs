using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using PCL.Core.Minecraft.Horizon;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>编译配置，对应官方启动器的 native-profile。</summary>
public enum HorizonNativeProfile
{
    /// <summary>默认：Cargo 的 fast 配置，增量编译，改模组后重编快。</summary>
    Performance,

    /// <summary>开发：horizon-client 不优化，重编最快但游戏约慢一倍。</summary>
    Dev,

    /// <summary>发布：thin LTO，编译最慢，游戏略快。</summary>
    Release
}

/// <summary>构建的目标实例（只用到路径，便于脱离界面运行）。</summary>
/// <param name="BaseVersion">实例所玩的 Nightly，如 nightly-2026-11-13-9f42064。</param>
public sealed record HorizonNativeTarget(string Name, string InstanceFolder, string GameDir, string BaseVersion)
{
    /// <summary>指定编译工作目录；为 null 时按 <see cref="HorizonNativeBuild.BuildRoot(string)" /> 选择。</summary>
    public string? BuildRootOverride { get; init; }

    public static HorizonNativeTarget Of(McInstance instance) => new(
        instance.Name,
        instance.PathInstance.TrimEnd('\\'),
        instance.PathIndie.TrimEnd('\\'),
        instance.JsonObject?[HorizonInstance.DataKey]?["version"]?.ToString()
        ?? throw new InvalidDataException("实例 json 中没有 horizon_data.version"));
}

/// <summary>一个要下载的文件：按顺序尝试地址，有 SHA-256 时校验。</summary>
public sealed record HorizonDownload(IReadOnlyList<string> Urls, string Path, string? Sha256, long MinSize = -1);

/// <summary>构建步骤所需的宿主：进度、取消与下载。界面里由加载器实现，测试里可以直接用 HttpClient 实现。</summary>
public interface IHorizonNativeHost
{
    CancellationToken Token { get; }
    void Progress(double fraction);
    void Download(string title, IReadOnlyList<HorizonDownload> files);
}

/// <summary>实例原生模组的状态。</summary>
public sealed record HorizonNativeStatus(
    HorizonNativeState State,
    IReadOnlyList<HorizonNativeBuiltMod> Enabled,
    string? PlayedVersion,
    HorizonNativeRecord? Record,
    IReadOnlyList<string> Problems);

/// <summary>
///     Horizon 原生模组（<c>.hnmod</c>）的构建：把打开的原生模组用 horizon-mixins 打进 Horizon 源码，再用固定版本的 Rust 编译出
///     新的 <c>horizon-client.exe</c>，作为实例的一个原生构建（<c>versions\&lt;实例&gt;\horizon\native\native-…\</c>）。
///     流程与官方启动器一致（docs/native-mods.md，“What the launcher does”）。
/// </summary>
/// <remarks>
///     与官方启动器的差异：只在实例所玩 Nightly 的提交上编译（官方首次构建也是这样）；不支持 <c>[api]</c>（trait）模组；
///     编译器报错时不按模组归因，直接失败并给出日志。
/// </remarks>
public static class HorizonNativeBuild
{
    public const string MixinsGit = "https://github.com/horizon-rust/horizon-mixins";
    public const string MixinsRevision = "5a64af938ec86b00d53d16a408aabc0038fb3e57";
    private const string HorizonRepository = "horizon-rust/horizon";
    private const string MixinsRepository = "horizon-rust/horizon-mixins";
    private const int SpareBuilds = 3;
    private const int CompileRetries = 3;

    private static readonly string[] _NotCopiedFromBase =
        ["horizon-client.exe", HorizonNativeRecord.FileName, "native-data", "native-content", ".installed", ".local"];

    private static int _building;

    public static bool IsBuilding => Volatile.Read(ref _building) != 0;

    /// <summary>实例的原生构建目录：<c>versions\&lt;实例&gt;\horizon\native</c>。</summary>
    public static string NativeVersionsDir(McInstance instance) => NativeVersionsDir(instance.PathInstance.TrimEnd('\\'));

    public static string NativeVersionsDir(string instanceFolder) => Path.Combine(HorizonInstance.Dir(instanceFolder), "native");

    private static string _CurrentFile(string instanceFolder) => Path.Combine(NativeVersionsDir(instanceFolder), "current");

    public static string BuildRoot(McInstance instance) => BuildRoot(instance.PathInstance.TrimEnd('\\'));

    /// <summary>编译工作目录（工具链、Cargo 缓存、源码、编译产物），须为纯 ASCII 无空格路径。</summary>
    public static string BuildRoot(string instanceFolder)
    {
        var minecraft = Path.GetFullPath(Path.Combine(instanceFolder, "..", ".."));
        return HorizonRustToolchain.ChooseBuildRoot(
            [Path.Combine(minecraft, "horizon-native"), Path.Combine(LauncherPaths.ExecutableDirectory, "PCL", "Horizon", "native-build")],
            minecraft);
    }

    public static string LogPath(McInstance instance) => Path.Combine(BuildRoot(instance), "native", "build.log");

    /// <summary>
    ///     实例启动时使用的构建目录：打开的原生模组已编进某个保留的原生构建时用它，否则用 Nightly 原版构建。
    /// </summary>
    public static string PlayedBuildDir(McInstance instance)
    {
        var status = GetStatus(instance);
        if (status.State != HorizonNativeState.NoMods && status.PlayedVersion is { } version)
            return Path.Combine(NativeVersionsDir(instance), version);
        return HorizonInstance.BuildDir(instance.PathInstance.TrimEnd('\\'));
    }

    /// <summary>
    ///     原生模组状态。当前构建与打开的模组不符、但某个保留的构建正好相符时，切换到那个构建（关掉再打开模组不必重编）。
    /// </summary>
    public static HorizonNativeStatus GetStatus(McInstance instance)
    {
        var gameDir = instance.PathIndie.TrimEnd('\\');
        var problems = new List<string>();
        var enabled = _EnabledMods(gameDir, problems);
        var versionsDir = NativeVersionsDir(instance);

        var folder = instance.PathInstance.TrimEnd('\\');
        var current = File.Exists(_CurrentFile(folder)) ? File.ReadAllText(_CurrentFile(folder)).Trim() : null;
        var record = current is null ? null : _ReadInstalledRecord(versionsDir, current);
        if (record is null) current = null;

        var state = HorizonNativeLogic.State(enabled, record);
        if (state is HorizonNativeState.NotBuilt or HorizonNativeState.NeedsRebuild && Directory.Exists(versionsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(versionsDir, "native-*")
                         .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, ".installed"))))
            {
                var kept = _ReadInstalledRecord(versionsDir, Path.GetFileName(dir));
                if (kept is null || HorizonNativeLogic.State(enabled, kept) != HorizonNativeState.UpToDate) continue;
                current = Path.GetFileName(dir);
                record = kept;
                state = HorizonNativeState.UpToDate;
                _SetCurrent(folder, current);
                LauncherCommon.Log($"[Horizon] 切换到已保留的原生构建 {current}");
                break;
            }
        }

        return new HorizonNativeStatus(state, enabled, current, record, problems);
    }

    private static HorizonNativeRecord? _ReadInstalledRecord(string versionsDir, string version)
    {
        var dir = Path.Combine(versionsDir, version);
        if (!File.Exists(Path.Combine(dir, ".installed")) || !File.Exists(Path.Combine(dir, HorizonProcess.ClientExe))) return null;
        return HorizonNativeRecord.TryRead(Path.Combine(dir, HorizonNativeRecord.FileName));
    }

    private static List<HorizonNativeBuiltMod> _EnabledMods(string gameDir, List<string>? problems)
    {
        var enabledIds = HorizonGameContent.ReadEnabled(gameDir);
        var result = new List<HorizonNativeBuiltMod>();
        var dir = HorizonGameContent.NativeModsPath(gameDir);
        if (!Directory.Exists(dir)) return result;
        foreach (var file in _NativeFiles(dir))
        {
            try
            {
                var bundle = HorizonNativeBundle.Read(file);
                if (enabledIds.Contains(bundle.Id)) result.Add(new HorizonNativeBuiltMod(bundle.Id, bundle.Version, bundle.SourceDigest));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // 读不出的文件总是算作需要构建（与官方一致），构建时会报出原因
                var stem = Path.GetFileNameWithoutExtension(file);
                if (enabledIds.Contains(stem)) result.Add(new HorizonNativeBuiltMod(stem, "", null));
                problems?.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        return result;
    }

    private static IEnumerable<string> _NativeFiles(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".hnmod", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".hmod", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static void _SetCurrent(string instanceFolder, string? version)
    {
        var file = _CurrentFile(instanceFolder);
        if (version is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, version);
    }

    #region 构建

    /// <summary>一次构建的上下文，在各步骤之间传递。</summary>
    private sealed class Job(HorizonNativeTarget target, HorizonNativeProfile profile) : IDisposable
    {
        public HorizonNativeTarget Target { get; } = target;
        public HorizonNativeProfile Profile { get; } = profile;
        public string InstanceFolder => Target.InstanceFolder;
        public string GameDir => Target.GameDir;
        public string BaseBuildDir => HorizonInstance.BuildDir(InstanceFolder);
        public string BaseVersion => Target.BaseVersion;
        public string BuildRoot { get; set; } = "";
        public string Native => Path.Combine(BuildRoot, "native");
        public List<HorizonNativeBundle> Bundles { get; } = [];
        public List<HorizonNativeLeftOut> LeftOut { get; } = [];
        public string Revision { get; set; } = "";
        public string Mixins { get; set; } = MixinsRevision;
        public int? SupportedHooks { get; set; }
        public string VersionName { get; set; } = "";
        public bool AlreadyBuilt { get; set; }
        public string CargoProfile { get; set; } = "fast";
        public List<string> ExtraConfig { get; } = [];
        public List<(string Mod, string Exe, IReadOnlyList<string> Args)> PostBuild { get; } = [];

        /// <summary>Cargo 是否经 crates.io 镜像下载；镜像出错时关掉，改为直连。</summary>
        public bool UseCratesMirror { get; set; } = true;
        public FileStream? Lock { get; set; }
        public StreamWriter? Log { get; set; }

        public void Write(string line)
        {
            lock (this) Log?.WriteLine(line);
        }

        public void Dispose()
        {
            Log?.Dispose();
            Log = null;
            Lock?.Dispose();
            Lock = null;
        }
    }

    /// <summary>
    ///     开始构建实例的原生模组，返回加入任务列表的加载器。同一时间只能有一个构建。
    /// </summary>
    /// <exception cref="InvalidOperationException">已有构建在进行。</exception>
    public static Loading.LoaderCombo<int> StartBuild(McInstance instance, HorizonNativeProfile profile, Action<Loading.LoaderBase>? onStateChanged)
    {
        var target = HorizonNativeTarget.Of(instance);
        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
            throw new InvalidOperationException("已经有一个原生构建在进行，请等它结束后再试。");

        var job = new Job(target, profile);
        Loading.LoaderTask<int, int> Step(string name, Action<Job, IHorizonNativeHost> run, double weight) =>
            new(name, task => run(job, new LoaderHost(task))) { ProgressWeight = weight, block = true };

        var loaders = new List<Loading.LoaderBase>
        {
            Step("准备原生构建", _Prepare, 1),
            Step("下载 Rust 工具链", _EnsureToolchain, 6),
            Step("下载 Horizon 源码", _EnsureSource, 2),
            Step("编译 horizon-mixins", _EnsureMixinsTool, 2),
            Step("编译游戏", _Compile, 12),
            Step("安装原生构建", _Install, 1)
        };
        var combo = new Loading.LoaderCombo<int>($"{instance.Name} 原生构建", loaders);
        combo.OnStateChanged = loader =>
        {
            if (loader.State is LoadState.Finished or LoadState.Failed or LoadState.Aborted)
            {
                if (loader.State == LoadState.Failed) job.Write("The build failed: " + loader.Error?.Message);
                job.Dispose();
                Interlocked.Exchange(ref _building, 0);
            }

            onStateChanged?.Invoke(loader);
        };
        try
        {
            combo.Start();
        }
        catch
        {
            job.Dispose();
            Interlocked.Exchange(ref _building, 0);
            throw;
        }

        return combo;
    }

    /// <summary>在当前线程依次执行全部步骤（不经过加载器），供测试与命令行使用。</summary>
    public static void RunBlocking(HorizonNativeTarget target, HorizonNativeProfile profile, IHorizonNativeHost host)
    {
        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
            throw new InvalidOperationException("已经有一个原生构建在进行，请等它结束后再试。");
        using var job = new Job(target, profile);
        try
        {
            foreach (var step in new Action<Job, IHorizonNativeHost>[]
                         { _Prepare, _EnsureToolchain, _EnsureSource, _EnsureMixinsTool, _Compile, _Install })
                step(job, host);
        }
        catch (Exception ex)
        {
            job.Write("The build failed: " + ex.Message);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _building, 0);
        }
    }

    /// <summary>用 PCL 的加载器实现构建宿主：进度显示在任务列表，下载走 PCL 的下载器。</summary>
    private sealed class LoaderHost(Loading.LoaderTask<int, int> task) : IHorizonNativeHost
    {
        public CancellationToken Token => task.AbortedToken;
        public void Progress(double fraction) => task.Progress = fraction;

        public void Download(string title, IReadOnlyList<HorizonDownload> files) =>
            new LoaderDownload(title, files
                    .Select(f => new DownloadFile(f.Urls, f.Path,
                        new FileChecker(minSize: f.MinSize, hash: f.Sha256, canUseExistsFile: f.Sha256 is not null)))
                    .ToList())
                .WaitForExit(loaderToSyncProgress: task);
    }

    private static void _Prepare(Job job, IHorizonNativeHost host)
    {
        job.BuildRoot = job.Target.BuildRootOverride ?? BuildRoot(job.InstanceFolder);
        Directory.CreateDirectory(job.Native);
        try
        {
            job.Lock = new FileStream(Path.Combine(job.Native, ".build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("另一个原生构建正在使用编译目录，请等它结束后再试。");
        }

        job.Log = new StreamWriter(Path.Combine(job.Native, "build.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        job.Write($"PCL native build of {job.Target.Name} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}), build root {job.BuildRoot}");
        LauncherCommon.Log($"[Horizon] 原生构建工作目录：{job.BuildRoot}");
        var needed = HorizonRustToolchain.IsInstalled(job.BuildRoot) ? HorizonRustToolchain.RebuildSpace : HorizonRustToolchain.FirstBuildSpace;
        if (HorizonRustToolchain.FreeSpace(job.BuildRoot) is { } free && free < needed)
            throw new IOException($"{Path.GetPathRoot(job.BuildRoot)} 只剩 {free / 1024 / 1024 / 1024.0:0.0} GB，原生构建至少需要 " +
                                  $"{needed / 1024 / 1024 / 1024} GB（编译目录 {job.BuildRoot}），请先清理磁盘。");

        if (!File.Exists(Path.Combine(job.BaseBuildDir, HorizonProcess.ClientExe)))
            throw new FileNotFoundException("Horizon 构建文件缺失，请重新安装这个 Horizon 实例。");

        // 打开的原生模组
        var enabledIds = HorizonGameContent.ReadEnabled(job.GameDir);
        var nativeDir = HorizonGameContent.NativeModsPath(job.GameDir);
        if (Directory.Exists(nativeDir))
        {
            foreach (var file in _NativeFiles(nativeDir))
            {
                HorizonNativeBundle bundle;
                try
                {
                    bundle = HorizonNativeBundle.Read(file);
                }
                catch (InvalidDataException ex)
                {
                    if (!enabledIds.Contains(Path.GetFileNameWithoutExtension(file))) continue;
                    throw new InvalidDataException($"{Path.GetFileName(file)} 不是有效的原生模组：{ex.Message}", ex);
                }

                if (!enabledIds.Contains(bundle.Id)) continue;
                if (job.Bundles.Any(b => b.Id == bundle.Id))
                    throw new InvalidDataException($"有两个 id 为 {bundle.Id} 的原生模组，请删掉其中一个。");
                job.Bundles.Add(bundle);
            }
        }

        if (job.Bundles.Count == 0)
            throw new InvalidOperationException($"{nativeDir} 中没有打开的原生模组：请先在模组页安装并打开原生模组。");
        host.Progress(0.2);

        // 所玩 Nightly 的提交与它支持的钩子版本
        var shortCommit = job.BaseVersion[(job.BaseVersion.LastIndexOf('-') + 1)..];
        job.Revision = _CommitSha(job, HorizonRepository, shortCommit, host.Token).Sha;
        job.SupportedHooks = HorizonProcess.QueryContentSupportAsync(job.BaseBuildDir, host.Token).GetAwaiter().GetResult().NativeHooks;
        job.Write($"Base {job.BaseVersion} = {job.HorizonShort()}, native hooks {job.SupportedHooks?.ToString() ?? "unknown"}");
        host.Progress(0.5);

        foreach (var bundle in job.Bundles.ToList())
        {
            if (bundle.UsesApi)
                _LeaveOut(job, bundle, "unsupported", null, "它使用了 [api] 扩展接口，本启动器暂不支持编译这类模组");
            else if (bundle.Manifest.Hooks is { } hooks && job.SupportedHooks is { } supported && hooks > supported)
                _LeaveOut(job, bundle, "other-version", null, $"需要钩子版本 {hooks}，当前 Horizon 只有 {supported}，请更新 Horizon");
        }

        if (job.Bundles.Count == 0)
            throw new InvalidOperationException("打开的原生模组都无法在当前 Horizon 上编译：\n" + string.Join("\n", job.LeftOut.Select(_Label)));

        // horizon-mixins 版本：模组钉住的版本；不一致时取提交最新的
        var pins = job.Bundles.Select(b => b.Manifest.Mixins).Distinct(StringComparer.Ordinal).ToList();
        job.Mixins = pins.Count switch
        {
            0 => MixinsRevision,
            1 => pins[0],
            _ => pins.Append(MixinsRevision).Distinct()
                .OrderByDescending(rev => _CommitSha(job, MixinsRepository, rev, host.Token).Date, StringComparer.Ordinal)
                .First()
        };

        (job.CargoProfile, var extra) = job.Profile switch
        {
            HorizonNativeProfile.Release => ("release", Array.Empty<string>()),
            HorizonNativeProfile.Dev => ("fast", ["profile.fast.package.horizon-client.opt-level=0"]),
            _ => ("fast", Array.Empty<string>())
        };
        job.ExtraConfig.AddRange(extra);
        _StageBundles(job);
        host.Progress(1);
    }

    /// <summary>把模组副本写进 mods.incoming（改钉到所玩的提交），并算出构建版本名；已有同样的构建时直接切换过去。</summary>
    private static void _StageBundles(Job job)
    {
        var incoming = Path.Combine(job.Native, "mods.incoming");
        if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        var keyed = new List<(string, IReadOnlyDictionary<string, byte[]>)>();
        foreach (var bundle in job.Bundles)
        {
            var toml = bundle.MixinToml;
            var pinned = HorizonToml.GetString(HorizonToml.Parse(toml), "compatibility.horizon_revision");
            if (bundle.Manifest.Hooks is not null || pinned != job.Revision)
                toml = HorizonNativeBundle.Repin(toml, job.Revision);
            bundle.ExtractTo(Path.Combine(incoming, bundle.Id), toml);
            var files = new Dictionary<string, byte[]>(bundle.Files, StringComparer.Ordinal)
            {
                [HorizonModFile.MixinManifest] = Encoding.UTF8.GetBytes(toml)
            };
            keyed.Add((bundle.Id, files));
        }

        job.VersionName = HorizonNativeLogic.VersionName(job.Mixins, job.CargoProfile, job.ExtraConfig, job.Revision, keyed);
        job.Write($"Mods: {string.Join(", ", job.Bundles.Select(b => $"{b.Id} {b.Version}"))}; horizon-mixins {job.Mixins[..7]}; version {job.VersionName}");

        var existing = Path.Combine(NativeVersionsDir(job.InstanceFolder), job.VersionName);
        if (File.Exists(Path.Combine(existing, ".installed")) && File.Exists(Path.Combine(existing, HorizonProcess.ClientExe)))
        {
            job.AlreadyBuilt = true;
            File.SetLastWriteTimeUtc(Path.Combine(existing, ".installed"), DateTime.UtcNow);
            job.Write("Up to date (no build needed).");
        }
    }

    private static void _EnsureToolchain(Job job, IHorizonNativeHost host)
    {
        if (job.AlreadyBuilt || HorizonRustToolchain.IsInstalled(job.BuildRoot)) return;
        var rustDir = Path.Combine(job.BuildRoot, "rust");
        var downloads = Path.Combine(rustDir, ".downloads");
        Directory.CreateDirectory(downloads);
        var components = HorizonRustToolchain.Components(HorizonRelease.BuiltInGithubProxies);
        job.Write($"Downloading Rust {HorizonRustToolchain.Version} and MinGW into {rustDir}");
        host.Download("下载 Rust 工具链", components
            .Select(c => new HorizonDownload(c.Urls, Path.Combine(downloads, c.FileName), c.Sha256))
            .ToList());

        var final = HorizonRustToolchain.ToolchainDir(job.BuildRoot);
        var partial = Path.Combine(rustDir, $".{HorizonRustToolchain.Version}-{HorizonRustToolchain.Target}.partial");
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);
        foreach (var component in components)
        {
            host.Token.ThrowIfCancellationRequested();
            var archive = Path.Combine(downloads, component.FileName);
            job.Write($"Unpacking {component.FileName}");
            if (component.Name == "mingw64") HorizonRustToolchain.ExtractWinLibs(archive, partial);
            else HorizonRustToolchain.ExtractRustComponent(archive, partial);
        }

        if (!File.Exists(Path.Combine(partial, "mingw64", "bin", "gcc.exe")))
            throw new InvalidDataException($"Rust {HorizonRustToolchain.Version} 的工具链里没有编译和链接需要的文件。");
        File.WriteAllText(Path.Combine(partial, ".installed"), HorizonRustToolchain.Version);
        if (Directory.Exists(final)) Directory.Delete(final, true);
        Directory.Move(partial, final);
        Directory.Delete(downloads, true);
        job.Write("Rust installed.");
    }

    private static void _EnsureSource(Job job, IHorizonNativeHost host)
    {
        if (job.AlreadyBuilt) return;
        var sourceRoot = Path.Combine(job.Native, "source");
        var target = Path.Combine(sourceRoot, job.Revision);
        if (File.Exists(Path.Combine(target, ".horizon-revision"))) return;

        Directory.CreateDirectory(sourceRoot);
        var archive = Path.Combine(sourceRoot, $".{job.Revision}.tar.gz");
        job.Write($"Downloading the source of {job.Revision}");
        // 源码会被编译并运行，只从 GitHub 直连下载，不经第三方加速站
        host.Download("下载 Horizon 源码", [
            new HorizonDownload([
                $"https://codeload.github.com/{HorizonRepository}/tar.gz/{job.Revision}",
                $"https://github.com/{HorizonRepository}/archive/{job.Revision}.tar.gz"
            ], archive, null, 1024 * 1024)
        ]);

        var partial = Path.Combine(sourceRoot, $".{job.Revision}.partial");
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);
        using (var file = File.OpenRead(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                // 只要普通文件与目录：链接一律跳过，构建也不需要它们
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) continue;
                var parts = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':'))) continue;
                var path = Path.Combine([partial, .. parts]);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, true);
            }
        }

        var top = Directory.GetDirectories(partial).FirstOrDefault()
                  ?? throw new InvalidDataException("Horizon 源码压缩包是空的");
        File.WriteAllText(Path.Combine(top, ".horizon-revision"), job.Revision);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(top, target);
        Directory.Delete(partial, true);
        File.Delete(archive);
        job.Write("Source ready.");
    }

    private static string _MixinsExe(Job job) =>
        Path.Combine(job.Native, "tools", job.Mixins[..12], "bin", "horizon-mixins.exe");

    private static void _EnsureMixinsTool(Job job, IHorizonNativeHost host)
    {
        if (job.AlreadyBuilt || File.Exists(_MixinsExe(job))) return;
        var root = Path.Combine(job.Native, "tools", job.Mixins[..12]);
        var progress = new HorizonRustToolchain.CargoProgress();
        var (exit, _, tail) = _Run(job, HorizonRustToolchain.CargoExe(job.BuildRoot),
            ["install", "--locked", "--git", MixinsGit, "--rev", job.Mixins, "--root", root, "horizon-mixins-cli"],
            lowPriority: true, host.Token,
            onStderr: line =>
            {
                var text = progress.Feed(line);
                if (progress.Fraction is { } f) host.Progress(f);
                return text;
            });
        if (exit != 0 || !File.Exists(_MixinsExe(job)))
            throw _Failure(job, "编译 horizon-mixins", exit, tail, []);
    }

    private static void _Compile(Job job, IHorizonNativeHost host)
    {
        if (job.AlreadyBuilt) return;
        var source = Path.Combine(job.Native, "source", job.Revision);
        var stageNew = Path.Combine(job.Native, "stage.new");
        var stage = Path.Combine(job.Native, "stage");

        for (var attempt = 0;; attempt++)
        {
            host.Token.ThrowIfCancellationRequested();
            if (Directory.Exists(stageNew)) Directory.Delete(stageNew, true);

            // 1. 应用 mixin（只有带注入内容的模组才需要）
            var withMixins = job.Bundles.Where(b => b.HasMixinsContent).ToList();
            if (withMixins.Count > 0)
            {
                var mixinsIncoming = Path.Combine(job.Native, "mixins.incoming");
                if (Directory.Exists(mixinsIncoming)) Directory.Delete(mixinsIncoming, true);
                foreach (var bundle in withMixins)
                    _CopyDirectory(Path.Combine(job.Native, "mods.incoming", bundle.Id), Path.Combine(mixinsIncoming, bundle.Id));

                var (exit, _, tail) = _Run(job, _MixinsExe(job),
                    ["apply", "--workspace", source, "--mods", mixinsIncoming, "--out", stageNew],
                    lowPriority: false, host.Token);
                Directory.Delete(mixinsIncoming, true);
                if (exit != 0)
                {
                    var (culprit, code, oneLine) = _MixinsDiagnostic(job, tail);
                    if (culprit is not null && attempt < CompileRetries && job.Bundles.Count > 1)
                    {
                        _LeaveOut(job, culprit, code == "E_MODULE_CONFLICT" ? "clash" : "other-version", null, oneLine);
                        _StageBundles(job);
                        if (job.AlreadyBuilt) return;
                        continue;
                    }

                    throw _Failure(job, "应用 mixin", exit, tail, [], oneLine);
                }
            }
            else
            {
                HorizonNativeLogic.Sync(source, stageNew);
            }

            host.Progress(0.05);

            // 2. 同步到固定的暂存目录：只写有变化的文件，Cargo 就只重编受影响的部分
            var stamped = Path.Combine(job.Native, "stage.stamped");
            if (!File.Exists(stamped))
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                File.WriteAllText(stamped, "");
            }

            var written = HorizonNativeLogic.Sync(stageNew, stage);
            Directory.Delete(stageNew, true);
            job.Write($"Stage: {written} files changed");

            // 3. 编译
            var profile = File.ReadAllText(Path.Combine(stage, "Cargo.toml")).Contains("[profile.fast]") ? job.CargoProfile : "release";
            var (package, bin) = File.Exists(Path.Combine(stage, "crates", "horizon-official", "Cargo.toml"))
                ? ("horizon-official", "horizon-official")
                : ("horizon-client", "horizon-client");
            var arguments = new List<string> { "build" };
            var sourceConfig = Path.Combine(stage, ".cargo", "config.toml");
            if (File.Exists(sourceConfig)) arguments.AddRange(["--config", sourceConfig]);
            arguments.AddRange(["--manifest-path", Path.Combine(stage, "Cargo.toml"), "--locked", "--message-format=json", "--profile", profile]);
            if (profile == "fast")
                foreach (var config in job.ExtraConfig) arguments.AddRange(["--config", config]);
            var clientToml = Path.Combine(stage, "crates", "horizon-client", "Cargo.toml");
            if (File.Exists(clientToml) && HorizonToml.Get(HorizonToml.Parse(File.ReadAllText(clientToml)), "features.mods") is not null)
                arguments.AddRange(["--features", "horizon-client/mods"]);
            arguments.AddRange(["-p", package, "--bin", bin]);

            var injections = Path.Combine(stage, ".horizon-mod-api-injections.json");
            var environment = new Dictionary<string, string?>
            {
                ["CARGO_TARGET_DIR"] = Path.Combine(job.Native, "target"),
                ["CARGO_TERM_PROGRESS_WHEN"] = "always",
                ["CARGO_TERM_PROGRESS_WIDTH"] = "100",
                ["HORIZON_MOD_API_INJECTIONS"] = File.Exists(injections) ? injections : null
            };
            var progress = new HorizonRustToolchain.CargoProgress();
            var errors = new List<string>();
            var started = Stopwatch.StartNew();
            var (buildExit, _, buildTail) = _Run(job, HorizonRustToolchain.CargoExe(job.BuildRoot), arguments,
                lowPriority: true, host.Token,
                onStderr: line =>
                {
                    var text = progress.Feed(line);
                    if (progress.Fraction is { } f) host.Progress(0.05 + 0.9 * f);
                    return text;
                },
                onStdout: line =>
                {
                    if (!line.StartsWith('{')) return line;
                    try
                    {
                        var json = JsonNode.Parse(line);
                        if (json?["reason"]?.GetValue<string>() != "compiler-message") return null;
                        var rendered = json["message"]?["rendered"]?.GetValue<string>();
                        if (json["message"]?["level"]?.GetValue<string>() == "error" && rendered is not null && errors.Count < 4)
                            errors.Add(rendered.TrimEnd());
                        return rendered?.TrimEnd();
                    }
                    catch (Exception)
                    {
                        return line;
                    }
                },
                extraEnvironment: environment);
            if (buildExit != 0) throw _Failure(job, "编译游戏", buildExit, buildTail, errors);
            job.Write($"Built in {started.Elapsed.TotalSeconds:0} s ({job.Profile}, {profile})");

            var targetDir = Path.Combine(job.Native, "target", profile);
            var programs = Path.Combine(job.Native, "programs");
            if (Directory.Exists(programs)) Directory.Delete(programs, true);
            Directory.CreateDirectory(programs);
            File.Copy(Path.Combine(targetDir, bin + ".exe"), Path.Combine(programs, HorizonProcess.ClientExe), true);

            // 4. 模组的构建后程序
            job.PostBuild.Clear();
            var postBuild = Path.Combine(stage, "native-post-build.json");
            if (File.Exists(postBuild) && JsonNode.Parse(File.ReadAllText(postBuild)) is JsonArray steps)
            {
                var i = 0;
                foreach (var step in steps.OfType<JsonObject>())
                {
                    var mod = step["mod"]?.GetValue<string>() ?? "";
                    var stepPackage = step["package"]?.GetValue<string>() ?? throw new InvalidDataException("native-post-build.json 缺少 package");
                    var stepBin = step["bin"]?.GetValue<string>() ?? throw new InvalidDataException("native-post-build.json 缺少 bin");
                    var stepArgs = new List<string> { "build" };
                    if (File.Exists(sourceConfig)) stepArgs.AddRange(["--config", sourceConfig]);
                    stepArgs.AddRange(["--manifest-path", Path.Combine(stage, "Cargo.toml"), "--locked", "--profile", profile, "-p", stepPackage, "--bin", stepBin]);
                    if (profile == "fast")
                        foreach (var config in job.ExtraConfig) stepArgs.AddRange(["--config", config]);
                    var (stepExit, _, stepTail) = _Run(job, HorizonRustToolchain.CargoExe(job.BuildRoot), stepArgs, true,
                        host.Token, extraEnvironment: new Dictionary<string, string?> { ["CARGO_TARGET_DIR"] = Path.Combine(job.Native, "target") });
                    if (stepExit != 0) throw _Failure(job, $"编译 {mod} 的构建后程序", stepExit, stepTail, []);
                    var dest = Path.Combine(programs, "post-build", i.ToString(), stepBin + ".exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(Path.Combine(targetDir, stepBin + ".exe"), dest, true);
                    job.PostBuild.Add((mod, dest,
                        (step["args"] as JsonArray ?? []).Select(a => a?.GetValue<string>()).OfType<string>().ToList()));
                    i++;
                }
            }

            host.Progress(1);
            return;
        }
    }

    private static void _Install(Job job, IHorizonNativeHost host)
    {
        var versionsDir = NativeVersionsDir(job.InstanceFolder);
        if (!job.AlreadyBuilt)
        {
            var partial = Path.Combine(versionsDir, $".{job.VersionName}.partial");
            var target = Path.Combine(versionsDir, job.VersionName);
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            Directory.CreateDirectory(partial);

            // 复制 Nightly 构建（资源、其他程序），换上编译出的 horizon-client.exe
            foreach (var entry in Directory.EnumerateFileSystemEntries(job.BaseBuildDir))
            {
                var name = Path.GetFileName(entry);
                if (_NotCopiedFromBase.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) _LinkDirectory(entry, Path.Combine(partial, name));
                else _LinkOrCopy(entry, Path.Combine(partial, name));
            }

            File.Copy(Path.Combine(job.Native, "programs", HorizonProcess.ClientExe), Path.Combine(partial, HorizonProcess.ClientExe), true);
            var record = new HorizonNativeRecord
            {
                Base = job.BaseVersion,
                Horizon = job.Revision,
                BaseHorizon = job.Revision,
                OnBase = true,
                HorizonMixins = job.Mixins,
                Hooks = job.Bundles.FirstOrDefault()?.Manifest.Hooks,
                Profile = job.Profile.ToString().ToLowerInvariant(),
                Mods = job.Bundles.Select(b => new HorizonNativeBuiltMod(b.Id, b.Version, b.SourceDigest)).ToList(),
                LeftOut = job.LeftOut
            };
            File.WriteAllText(Path.Combine(partial, HorizonNativeRecord.FileName), record.ToJson(), new UTF8Encoding(false));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(partial, target);

            try
            {
                foreach (var (mod, exe, args) in job.PostBuild)
                {
                    var data = Path.Combine(target, "native-data", mod);
                    var cache = Path.Combine(job.Native, "cache", mod);
                    Directory.CreateDirectory(data);
                    Directory.CreateDirectory(cache);
                    var info = new ProcessStartInfo(exe)
                    {
                        WorkingDirectory = target, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    foreach (var arg in args) info.ArgumentList.Add(arg);
                    info.Environment["HORIZON_NATIVE_MOD"] = mod;
                    info.Environment["HORIZON_NATIVE_VERSION_DIR"] = target;
                    info.Environment["HORIZON_NATIVE_DATA_DIR"] = data;
                    info.Environment["HORIZON_NATIVE_CACHE_DIR"] = cache;
                    info.Environment["HORIZON_NATIVE_BUNDLE_DATA_DIR"] = Path.Combine(job.Native, "stage", "native-data", mod);
                    var (exit, _, tail) = _Run(job, info, true, host.Token);
                    if (exit != 0) throw _Failure(job, $"{mod} 的构建后程序", exit, tail, []);
                }
            }
            catch
            {
                Directory.Delete(target, true);
                throw;
            }

            File.WriteAllText(Path.Combine(target, ".installed"), "");
            var programs = Path.Combine(job.Native, "programs");
            if (Directory.Exists(programs)) Directory.Delete(programs, true);
            job.Write("Build completed.");
        }

        _SetCurrent(job.InstanceFolder, job.VersionName);
        _Prune(versionsDir, job.VersionName);
        LauncherCommon.Log($"[Horizon] 实例 {job.Target.Name} 现在使用原生构建 {job.VersionName}" +
                           (job.LeftOut.Count > 0 ? "；未编入：" + string.Join("；", job.LeftOut.Select(_Label)) : ""));
    }

    /// <summary>只保留当前构建与最近使用的 3 个构建。</summary>
    private static void _Prune(string versionsDir, string keep)
    {
        if (!Directory.Exists(versionsDir)) return;
        var old = Directory.EnumerateDirectories(versionsDir, "native-*")
            .Where(d => Path.GetFileName(d) != keep)
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, ".installed")))
            .Skip(SpareBuilds);
        foreach (var dir in old)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LauncherCommon.Log($"[Horizon] 无法删除旧的原生构建 {dir}：{ex.Message}");
            }
        }
    }

    #endregion

    #region 工具

    private static string HorizonShort(this Job job) => job.Revision.Length >= 7 ? job.Revision[..7] : job.Revision;

    private static void _LeaveOut(Job job, HorizonNativeBundle bundle, string why, string? with, string reason)
    {
        job.Bundles.Remove(bundle);
        job.LeftOut.Add(new HorizonNativeLeftOut(bundle.Id, bundle.Version, why, with));
        job.Write($"warning: {bundle.Id} was left out: {reason}");
        LauncherCommon.Log($"[Horizon] 原生模组 {bundle.Id} {bundle.Version} 未编入：{reason}");
    }

    private static string _Label(HorizonNativeLeftOut left) => left.Why switch
    {
        "clash" => $"{left.Id}：与其他模组冲突",
        "needs" => $"{left.Id}：依赖的模组未编入",
        "unsupported" => $"{left.Id}：使用了暂不支持的 [api] 接口",
        _ => $"{left.Id}：为其他 Horizon 版本制作"
    };

    private static (HorizonNativeBundle? Culprit, string? Code, string OneLine) _MixinsDiagnostic(Job job, IReadOnlyList<string> tail)
    {
        var text = string.Join("\n", tail);
        var brace = text.IndexOf('{');
        if (brace < 0) return (null, null, tail.LastOrDefault() ?? "");
        try
        {
            if (JsonNode.Parse(text[brace..]) is not JsonObject json) return (null, null, text);
            var code = json["code"]?.GetValue<string>();
            var message = json["message"]?.GetValue<string>() ?? "";
            var modId = json["mod_id"]?.GetValue<string>();
            var file = json["file"]?.GetValue<string>();
            if (modId is null && file is not null)
            {
                var parts = file.Replace('\\', '/').Split('/');
                var index = Array.FindIndex(parts, p => p is "mixins.incoming" or "mods.incoming");
                if (index >= 0 && index + 1 < parts.Length) modId = parts[index + 1];
            }

            var oneLine = $"{modId ?? "?"}: {code}: {message}{(file is null ? "" : $" ({file})")}";
            return (job.Bundles.FirstOrDefault(b => b.Id == modId), code, oneLine);
        }
        catch (Exception)
        {
            return (null, null, text);
        }
    }

    private static Exception _Failure(Job job, string what, int exit, IReadOnlyList<string> tail, IReadOnlyList<string> errors,
        string? diagnostic = null)
    {
        var all = string.Join("\n", tail);
        var sb = new StringBuilder();
        if (all.Contains("os error 4551")) sb.Append(HorizonRustToolchain.SmartAppControlMessage).Append("\n\n");
        sb.Append($"{what}失败（退出码 {exit}）。完整日志：{Path.Combine(job.Native, "build.log")}\n");
        if (errors.Count > 0) sb.Append(string.Join("\n", errors)).Append('\n');
        sb.Append(diagnostic ?? string.Join("\n", tail.TakeLast(12)));
        if (job.Bundles.Any(b => b.Manifest.Horizon != job.Revision))
            sb.Append($"\n\n部分模组是为其他 Horizon 版本制作的（当前编译 {job.HorizonShort()}），这可能是失败原因：请安装为同一 Nightly 制作的版本。");
        return new InvalidOperationException(sb.ToString());
    }

    private static (string Sha, string Date) _CommitSha(Job job, string repository, string reference, CancellationToken token)
    {
        var cacheFile = Path.Combine(job.Native, "commit-cache.json");
        var key = $"{repository}@{reference}";
        JsonObject cache;
        try
        {
            cache = File.Exists(cacheFile) ? JsonNode.Parse(File.ReadAllText(cacheFile)) as JsonObject ?? [] : [];
        }
        catch (Exception)
        {
            cache = [];
        }

        if (cache[key] is JsonObject hit && hit["sha"]?.GetValue<string>() is { Length: 40 } sha)
            return (sha, hit["date"]?.GetValue<string>() ?? "");

        var result = HorizonRelease.GetCommitAsync(HorizonInstance.Http, repository, reference, token).GetAwaiter().GetResult();
        cache[key] = new JsonObject { ["sha"] = result.Sha, ["date"] = result.Date };
        File.WriteAllText(cacheFile, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private static (int Exit, string Stdout, IReadOnlyList<string> Tail) _Run(Job job, string program, IReadOnlyList<string> arguments,
        bool lowPriority, CancellationToken token,
        Func<string, string?>? onStderr = null, Func<string, string?>? onStdout = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var isCargo = Path.GetFileName(program).Equals("cargo.exe", StringComparison.OrdinalIgnoreCase);
        while (true)
        {
            IReadOnlyList<string> args = isCargo && job.UseCratesMirror && arguments.Count > 0
                ? [arguments[0], .. HorizonRustToolchain.CratesMirrorConfig.SelectMany(c => new[] { "--config", c }), .. arguments.Skip(1)]
                : arguments;
            var result = _Run(job, HorizonRustToolchain.StartInfo(job.BuildRoot, program, args, extraEnvironment), lowPriority, token,
                onStderr, onStdout);
            if (result.Exit == 0 || !isCargo || !job.UseCratesMirror || !_LooksLikeDownloadFailure(result.Tail)) return result;
            job.UseCratesMirror = false;
            job.Write("warning: the crates.io mirror failed, retrying with crates.io directly");
            LauncherCommon.Log("[Horizon] crates.io 镜像下载失败，改为直连重试");
        }
    }

    private static bool _LooksLikeDownloadFailure(IReadOnlyList<string> tail) =>
        tail.Any(line => line.Contains("failed to download", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("spurious network error", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("failed to get", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("failed to query replaced source", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("failed to load source for dependency", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("network failure", StringComparison.OrdinalIgnoreCase));

    /// <summary>运行进程，输出写入构建日志；返回退出码、标准输出与标准错误的最后 12 行。</summary>
    private static (int Exit, string Stdout, IReadOnlyList<string> Tail) _Run(Job job, ProcessStartInfo info, bool lowPriority,
        CancellationToken token, Func<string, string?>? onStderr = null, Func<string, string?>? onStdout = null)
    {
        job.Write("$ " + info.FileName + " " + string.Join(' ', info.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var tail = new Queue<string>();
        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var text = onStderr is null ? e.Data : onStderr(e.Data);
            if (string.IsNullOrEmpty(text)) return;
            job.Write(text);
            lock (tail)
            {
                foreach (var line in text.Split('\n'))
                {
                    tail.Enqueue(line);
                    if (tail.Count > 12) tail.Dequeue();
                }
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            var text = onStdout is null ? e.Data : onStdout(e.Data);
            if (!string.IsNullOrEmpty(text)) job.Write(text);
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 4551)
        {
            throw new InvalidOperationException(HorizonRustToolchain.SmartAppControlMessage, ex);
        }

        if (lowPriority)
        {
            try
            {
                // 后续由它启动的 rustc 继承低于正常的优先级，桌面和正在玩的游戏不会卡
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // 进程已退出
            }
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using (token.Register(() =>
               {
                   try
                   {
                       if (!process.HasExited) process.Kill(true);
                   }
                   catch (Exception)
                   {
                       // 已退出
                   }
               }))
        {
            process.WaitForExit();
        }

        token.ThrowIfCancellationRequested();
        lock (tail) return (process.ExitCode, stdout.ToString(), tail.ToList());
    }

    /// <summary>
    ///     把文件夹以硬链接方式“复制”过去（同一分区时几乎不占空间），做不到时退回真正复制。
    ///     原生构建与 Nightly 构建只有 horizon-client.exe 不同，其余文件共用。
    /// </summary>
    private static void _LinkDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            _LinkOrCopy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void _LinkOrCopy(string source, string target)
    {
        if (File.Exists(target)) File.Delete(target);
        if (!_CreateHardLink(target, source, IntPtr.Zero)) File.Copy(source, target, true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool _CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static void _CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }

    #endregion
}
