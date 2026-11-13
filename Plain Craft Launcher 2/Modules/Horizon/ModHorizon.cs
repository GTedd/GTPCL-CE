using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.Minecraft.Horizon;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI.Hints;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     在 PCL 中安装、准备并启动 Horizon（horizon-rust/horizon，Rust 重写的 Java 版 26.3）。
///     遵循 Horizon 给第三方启动器的 Launch API（docs/api/launch-guide.md）：
///     构建按签名校验后再解压；资源由 horizon-prepare 从本机已有的 26.3 文件准备；
///     启动时把当前微软账号的会话从标准输入交给 horizon-client。
/// </summary>
public static class ModHorizon
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static Loading.LoaderCombo<int>? _running;

    /// <summary>Horizon 的数据根目录：启动器目录下的 PCL\Horizon。</summary>
    public static string Root => Path.Combine(Paths.Data, "Horizon");

    private static string BuildsDir => Path.Combine(Root, "builds");
    private static string PreparedRoot => Path.Combine(Root, "prepared");
    private static string GameDir => Path.Combine(Root, "game");
    private static string StatePath => Path.Combine(Root, "state.json");

    /// <summary>已安装的构建版本（nightly-日期-提交），未安装时为 null。</summary>
    public static string? InstalledVersion
    {
        get
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                var version = JsonNode.Parse(File.ReadAllText(StatePath))?["installed"]?.GetValue<string>();
                return version is not null && File.Exists(Path.Combine(BuildDir(version), ".installed")) ? version : null;
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Horizon] 读取安装状态失败");
                return null;
            }
        }
    }

    public static bool IsRunning => _running is { State: LoadState.Loading };

    private static string BuildDir(string version) => Path.Combine(BuildsDir, version);
    private static string PreparedDir(string version) => Path.Combine(PreparedRoot, version);

    private static IReadOnlyList<string> GithubProxyPrefixes =>
        (Config.Tool.HorizonGithubProxy ?? "")
        .Split(['\r', '\n', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(prefix => prefix.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        .ToList();

    /// <summary>
    ///     安装（或更新到最新 Nightly）并启动 Horizon。<paramref name="update" /> 为 true 时总是检查最新版本。
    /// </summary>
    public static void Start(bool update)
    {
        if (IsRunning)
        {
            HintService.Show(Hints.Info("Horizon 正在准备或启动中，请在任务列表中查看进度。"));
            return;
        }

        var context = new HorizonRun { Update = update };
        var loader = new Loading.LoaderCombo<int>("启动 Horizon", [
            new Loading.LoaderTask<int, int>("获取 Horizon 构建", task => _EnsureBuild(task, context)) { ProgressWeight = 6 },
            new Loading.LoaderTask<int, int>("查找本机的 Minecraft 文件", task => _EnsureGameFiles(task, context)) { ProgressWeight = 3 },
            new Loading.LoaderTask<int, int>("获取 Java", task => _EnsureJava(task, context)) { ProgressWeight = 1 },
            new Loading.LoaderTask<int, int>("准备 Horizon 资源", task => _EnsurePrepared(task, context)) { ProgressWeight = 8 },
            new Loading.LoaderTask<int, int>("登录微软账号", task => _Login(task, context)) { ProgressWeight = 1 },
            new Loading.LoaderTask<int, int>("启动 Horizon", task => _Launch(task, context)) { ProgressWeight = 1 }
        ])
        {
            Identifier = "Horizon",
            OnStateChanged = state =>
            {
                if (state.State == LoadState.Failed && state.Error is not null && state.Error is not CancelledException)
                    HintService.Show(Hints.Error("Horizon：" + state.Error.Message));
                Launcher.frmToolsHorizon?.RefreshStatus();
            }
        };
        _running = loader;
        loader.Start();
        Loading.LoaderTaskbarAdd(loader);
        Launcher.frmMain?.BtnExtraDownload.ShowRefresh();
        Launcher.frmMain?.BtnExtraDownload.Ribble();
        Launcher.frmToolsHorizon?.RefreshStatus();
    }

    /// <summary>打开 Horizon 的数据目录。</summary>
    public static void OpenFolder()
    {
        Directory.CreateDirectory(Root);
        Process.Start(new ProcessStartInfo(Root) { UseShellExecute = true });
    }

    #region 构建

    private static void _EnsureBuild(Loading.LoaderTask<int, int> task, HorizonRun context)
    {
        var installed = InstalledVersion;
        if (installed is not null && !context.Update)
        {
            context.Version = installed;
            Common.Log($"[Horizon] 使用已安装的构建 {installed}");
            return;
        }

        var remote = HorizonRelease.GetLatestNightlyAsync(Http, GithubProxyPrefixes, task.AbortedToken)
            .GetAwaiter().GetResult();
        Common.Log($"[Horizon] 最新构建：{remote.Title}（{remote.Version}）");
        if (remote.Version == installed)
        {
            context.Version = installed;
            HintService.Show(Hints.Info($"Horizon 已是最新版本（{remote.Title}）。"));
            return;
        }

        var staging = Path.Combine(BuildsDir, ".download-" + remote.Version);
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, remote.AssetName);
        var sums = Path.Combine(staging, HorizonReleaseVerifier.SumsFileName);
        var signature = Path.Combine(staging, HorizonReleaseVerifier.NightlySignatureFileName);
        // 清单与签名很小，每次都重新下载；压缩包在校验通过前不使用。
        foreach (var small in new[] { sums, signature })
            if (File.Exists(small)) File.Delete(small);
        new LoaderDownload("下载 Horizon 构建", [
            new DownloadFile(remote.SumsUrls, sums),
            new DownloadFile(remote.SignatureUrls, signature),
            new DownloadFile(remote.AssetUrls, archive, new FileChecker(actualSize: remote.AssetSize > 0 ? remote.AssetSize : -1, canUseExistsFile: false))
        ]).WaitForExit(loaderToSyncProgress: task);

        var digest = HorizonReleaseVerifier.SignedDigest(
            remote.Version, File.ReadAllBytes(sums), File.ReadAllText(signature), remote.AssetName);
        var actual = HorizonReleaseVerifier.Sha256OfFile(archive);
        if (!string.Equals(digest, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载的 {remote.AssetName} 与签名清单不符（{actual}），已删除，请重试。");
        Common.Log($"[Horizon] 构建 {remote.Version} 的签名与 SHA-256 校验通过");

        var target = BuildDir(remote.Version);
        var extract = target + ".extracting";
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(archive, extract);
        // 部分构建的压缩包内有一层顶级文件夹
        var content = File.Exists(Path.Combine(extract, HorizonProcess.ClientExe))
            ? extract
            : Directory.GetDirectories(extract).FirstOrDefault(dir => File.Exists(Path.Combine(dir, HorizonProcess.ClientExe)))
              ?? throw new InvalidDataException("Horizon 构建中没有 horizon-client.exe");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(content, target);
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        File.WriteAllText(Path.Combine(target, ".installed"), remote.Title);
        Directory.Delete(staging, true);

        var previous = installed;
        _WriteState(remote.Version);
        context.Version = remote.Version;
        HintService.Show(Hints.Success($"已安装 Horizon {remote.Title}"));
        // 新构建能启动之前保留上一个构建，再旧的删除以节省空间
        _CleanOldBuilds(keep: [remote.Version, previous]);
    }

    private static void _WriteState(string version)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(StatePath, new JsonObject { ["installed"] = version }.ToJsonString());
    }

    private static void _CleanOldBuilds(IEnumerable<string?> keep)
    {
        var kept = keep.OfType<string>().ToHashSet();
        foreach (var dir in new[] { BuildsDir, PreparedRoot }.Where(Directory.Exists).SelectMany(Directory.GetDirectories))
        {
            if (kept.Contains(Path.GetFileName(dir))) continue;
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                Common.Log(ex, $"[Horizon] 清理旧构建失败：{dir}");
            }
        }
    }

    #endregion

    #region 游戏文件

    private static void _EnsureGameFiles(Loading.LoaderTask<int, int> task, HorizonRun context)
    {
        var buildDir = BuildDir(context.Version!);
        context.Pins = HorizonPins.Read(buildDir);
        var pins = context.Pins;

        var folders = new List<string>();
        if (!string.IsNullOrEmpty(GameFolders.mcFolderSelected)) folders.Add(GameFolders.mcFolderSelected);
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
        var client = HorizonGameFiles.FindClient(folders, pins)
                     ?? throw new FileNotFoundException(
                         $"没有找到 Minecraft {pins.Version} 的原版客户端。请先在 PCL 的下载页安装原版 {pins.Version} 并启动一次，再启动 Horizon。");
        Common.Log($"[Horizon] 使用本机的 {pins.Version} 客户端：{client.ClientJar}");
        context.Client = client;

        var downloads = new List<DownloadFile>();
        // 启动器改写过的版本 JSON 无法通过校验，改用官方原版
        var versionJson = client.VersionJson;
        if (versionJson is null)
        {
            versionJson = Path.Combine(Root, "minecraft", $"{pins.Version}.json");
            if (!pins.IsAcceptedManifest(versionJson))
                downloads.Add(new DownloadFile(HorizonGameFiles.ManifestUrls(pins), versionJson,
                    new FileChecker(hash: pins.ManifestSha1s[0], canUseExistsFile: false, isJson: true)));
        }

        context.VersionJson = versionJson;
        context.ServerJar = Path.Combine(Root, "server-jars", $"{pins.Version}.jar");
        if (downloads.Count > 0)
            new LoaderDownload("下载 Minecraft 版本文件", downloads).WaitForExit(loaderToSyncProgress: task);
        if (!File.Exists(context.ServerJar) ||
            !string.Equals(HorizonGameFiles.Sha1OfFile(context.ServerJar), pins.ServerSha1, StringComparison.OrdinalIgnoreCase))
            new LoaderDownload("下载 Minecraft 服务端", [
                new DownloadFile(HorizonGameFiles.ServerJarUrls(pins, versionJson), context.ServerJar,
                    new FileChecker(hash: pins.ServerSha1))
            ]).WaitForExit(loaderToSyncProgress: task);
    }

    #endregion

    #region Java

    private static void _EnsureJava(Loading.LoaderTask<int, int> task, HorizonRun context)
    {
        var major = context.Pins!.JavaMajor;
        context.JavaHome = _SelectJava(major);
        if (context.JavaHome is not null) return;

        if (!JavaRuntime.JavaDownloadConfirm($"Java {major}"))
            throw new CancelledException();
        var component = JsonNode.Parse(File.ReadAllText(context.VersionJson!))?["javaVersion"]?["component"]?.GetValue<string>()
                        ?? major.ToString();
        var javaLoader = JavaRuntime.GetJavaDownloadLoader();
        try
        {
            javaLoader.Start(component, true);
            while (javaLoader.State == LoadState.Loading && !task.IsAborted)
            {
                task.Progress = javaLoader.Progress;
                Thread.Sleep(10);
            }
        }
        finally
        {
            javaLoader.Abort();
        }

        context.JavaHome = _SelectJava(major)
                           ?? throw new InvalidOperationException($"没有找到 Java {major}，Horizon 准备资源需要它。");
    }

    private static string? _SelectJava(int major)
    {
        JavaEntry? java;
        lock (JavaRuntime.JavaLock)
        {
            java = JavaRuntime.JavaSelect("$$", new Version(major, 0), new Version(major, 999),
                null, true, new HashSet<int> { major });
        }

        if (java is null) return null;
        Common.Log($"[Horizon] 使用 Java：{java}");
        // JavaFolder 是 bin 目录，Java 主目录是它的上一级
        return Path.GetDirectoryName(java.Installation.JavaFolder.TrimEnd('\\', '/'));
    }

    #endregion

    #region 准备资源

    private static void _EnsurePrepared(Loading.LoaderTask<int, int> task, HorizonRun context)
    {
        var buildDir = BuildDir(context.Version!);
        var prepared = PreparedDir(context.Version!);
        Directory.CreateDirectory(prepared);
        var gaps = HorizonProcess.CheckPreparedAsync(buildDir, prepared, task.AbortedToken).GetAwaiter().GetResult();
        if (gaps is null)
        {
            Common.Log("[Horizon] 资源已是最新，跳过准备");
            return;
        }

        Common.Log($"[Horizon] 需要准备资源：{gaps}");
        var api = HorizonProcess.QueryPrepareApiAsync(buildDir, task.AbortedToken).GetAwaiter().GetResult();
        if (api.Minecraft != context.Pins!.Version)
            throw new InvalidDataException($"构建需要 Minecraft {api.Minecraft}，但 research-lock.json 固定的是 {context.Pins.Version}");
        var client = context.Client!;
        var arguments = new List<string> { "--prepare", "--output", prepared, "--complete", "--progress", "json" };

        void Add(string option, string value)
        {
            if (!api.Options.Contains(option))
                throw new NotSupportedException($"这个 Horizon 构建不支持 {option}，请更新 Horizon。");
            arguments.Add(option);
            arguments.Add(value);
        }

        Add("--server-jar", context.ServerJar!);
        Add("--version-json", context.VersionJson!);
        Add("--client-jar", client.ClientJar);
        Add("--libraries-dir", client.LibrariesDir);
        Add("--assets-dir", client.AssetsDir);
        if (context.JavaHome is not null) Add("--java-home", context.JavaHome);

        HorizonProcess.RunPrepareAsync(buildDir, arguments, context.JavaHome,
                message => Common.Log("[Horizon] " + message),
                fraction => task.Progress = fraction,
                task.AbortedToken)
            .GetAwaiter().GetResult();
        Common.Log("[Horizon] 资源准备完成");
    }

    #endregion

    #region 登录与启动

    private static void _Login(Loading.LoaderTask<int, int> task, HorizonRun context)
    {
        // Horizon 的 Launch API 要求只为拥有 Java 版的微软账号启动，并交出该账号的会话
        if (ProfileService.Current is not { ProfileType: ProfileType.Microsoft })
            throw new InvalidOperationException("Horizon 需要微软正版账号：请在启动页选择或添加一个微软账号后再启动。");
        GameLaunch.mcLoginMsLoader.WaitForExit(new GameLaunch.McLoginMs(), task);
        var login = GameLaunch.mcLoginMsLoader.output;
        context.Session = HorizonProcess.SessionJson(login.Name, login.Uuid, login.AccessToken, _XuidFromToken(login.AccessToken));
    }

    private static void _Launch(Loading.LoaderTask<int, int> task, HorizonRun context)
    {
        var buildDir = BuildDir(context.Version!);
        var options = HorizonProcess.QueryClientOptionsAsync(buildDir, task.AbortedToken).GetAwaiter().GetResult();
        Directory.CreateDirectory(GameDir);
        var arguments = new List<string>
        {
            "--resources", PreparedDir(context.Version!),
            "--game-dir", GameDir,
            "--home", GameDir
        };
        if (options.Contains("--session-stdin")) arguments.Add("--session-stdin");
        else throw new NotSupportedException("这个 Horizon 构建不支持 --session-stdin，请更新 Horizon。");

        var process = HorizonProcess.StartClient(buildDir, arguments, GameDir, context.Session);
        context.Session = null;
        Common.Log($"[Horizon] 已启动 horizon-client（PID {process.Id}）");
        HintService.Show(Hints.Success("Horizon 已启动"));
        _WatchExit(process);
    }

    private static void _WatchExit(Process process)
    {
        Common.RunInNewThread(() =>
        {
            var lastLine = "";
            try
            {
                while (process.StandardError.ReadLine() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) lastLine = line;
                    Common.Log("[Horizon] " + line);
                }

                process.WaitForExit();
                Common.Log($"[Horizon] horizon-client 退出，退出码 {process.ExitCode}");
                switch (process.ExitCode)
                {
                    case 0:
                        break;
                    case 2:
                        HintService.Show(Hints.Error("Horizon 拒绝了启动参数：" + lastLine));
                        break;
                    default:
                        HintService.Show(Hints.Error($"Horizon 异常退出（{process.ExitCode}）：{lastLine}"));
                        break;
                }
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Horizon] 监视进程失败");
            }
            finally
            {
                process.Dispose();
            }
        }, "Horizon Watcher");
    }

    /// <summary>
    ///     Minecraft 的访问令牌是 JWT，其中带有 xuid；读不到时返回 null（会话 JSON 中 xuid 可选）。
    /// </summary>
    private static string? _XuidFromToken(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))?["xuid"]?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    private sealed class HorizonRun
    {
        public bool Update;
        public string? Version;
        public HorizonPins? Pins;
        public HorizonLocalClient? Client;
        public string? VersionJson;
        public string? ServerJar;
        public string? JavaHome;
        public string? Session;
    }
}
