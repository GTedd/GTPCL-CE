using System.IO.Compression;
using System.Net.Http;
using PCL.Core.Minecraft.Horizon;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     安装页可选的 Horizon 构建：本平台全部可安装的版本（从新到旧，第一个为最新）及最新版本固定的 Minecraft 版本。
/// </summary>
public sealed record HorizonCandidate(IReadOnlyList<HorizonRemoteBuild> Builds, string Minecraft)
{
    public HorizonRemoteBuild Latest => Builds[0];
}

/// <summary>
///     Horizon 实例（horizon-rust/horizon，Rust 重写的 Java 版）。
///     它是一个普通的版本隔离实例：<c>versions\&lt;名称&gt;\</c> 中照常放原版 json 与 jar，
///     json 中多一项 <c>horizon_data</c>；Horizon 自身的文件都在 <c>versions\&lt;名称&gt;\horizon\</c>：
///     <c>build\</c> 为校验过签名的构建，<c>resources\</c> 为 horizon-prepare 准备的资源，
///     另有固定版本的官方 json 与服务端 jar。游戏目录跟随实例的版本隔离设置（PathIndie）。
/// </summary>
public static class HorizonInstance
{
    public const string DataKey = "horizon_data";

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    ///     Horizon 的全部可安装版本及最新版本的 Minecraft 版本，供安装页与实例的“修改”页使用。
    /// </summary>
    public static readonly Loading.LoaderTask<int, HorizonCandidate> DlHorizonListLoader =
        new("DlHorizonList Main", loader =>
        {
            var builds = HorizonRelease.ParseBuilds(_GetGameReleases(loader.AbortedToken), HorizonRelease.BuiltInGithubProxies);
            if (builds.Count == 0) throw new InvalidDataException("没有本平台可以安装的 Horizon 版本");
            var minecraft = GetMinecraftVersion(builds[0], loader.AbortedToken);
            LauncherCommon.Log($"[Horizon] 共 {builds.Count} 个可安装版本，最新：{builds[0].Title}（Minecraft {minecraft}）");
            loader.output = new HorizonCandidate(builds, minecraft);
        });

    private static string CacheFolder => Path.Combine(LauncherPaths.Temp, "Cache");
    private static string ReleasesCachePath => Path.Combine(CacheFolder, "HorizonReleases.json");
    private static string MinecraftCachePath => Path.Combine(CacheFolder, "HorizonMinecraft.json");
    private static readonly TimeSpan ReleasesCacheFresh = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     游戏版本的发布列表。10 分钟内直接用磁盘缓存（GitHub API 未登录每小时只有 60 次，重启启动器也不重复请求）；
    ///     请求失败时用旧缓存。
    /// </summary>
    private static JsonArray _GetGameReleases(CancellationToken token)
    {
        JsonArray? cached = null;
        try
        {
            if (File.Exists(ReleasesCachePath))
            {
                cached = JsonNode.Parse(File.ReadAllText(ReleasesCachePath)) as JsonArray;
                if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(ReleasesCachePath) < ReleasesCacheFresh)
                    return cached;
            }
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Horizon] 读取版本列表缓存失败");
            cached = null;
        }

        try
        {
            var releases = HorizonRelease.FetchGameReleasesAsync(Http, token).GetAwaiter().GetResult();
            try
            {
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(ReleasesCachePath, releases.ToJsonString());
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Horizon] 写入版本列表缓存失败");
            }

            return releases;
        }
        catch (Exception ex) when (cached is not null && ex is not OperationCanceledException)
        {
            LauncherCommon.Log(ex, "[Horizon] 获取版本列表失败，改用缓存");
            return cached;
        }
    }

    private static readonly object MinecraftCacheLock = new();
    private static Dictionary<string, string>? _minecraftVersions;

    /// <summary>
    ///     构建固定的 Minecraft 版本（读该构建提交的 research-lock.json）。同一构建不会变，结果按签名版本缓存到磁盘。
    /// </summary>
    public static string GetMinecraftVersion(HorizonRemoteBuild build, CancellationToken token = default)
    {
        lock (MinecraftCacheLock)
        {
            if (_minecraftVersions is null)
            {
                _minecraftVersions = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    if (File.Exists(MinecraftCachePath) &&
                        JsonNode.Parse(File.ReadAllText(MinecraftCachePath)) is JsonObject saved)
                        foreach (var (version, minecraft) in saved)
                            if (minecraft?.GetValue<string>() is { Length: > 0 } value)
                                _minecraftVersions[version] = value;
                }
                catch (Exception ex)
                {
                    LauncherCommon.Log(ex, "[Horizon] 读取 Minecraft 版本缓存失败");
                }
            }

            if (_minecraftVersions.TryGetValue(build.Version, out var known)) return known;
        }

        var result = HorizonRelease.GetMinecraftVersionAsync(Http, build, token).GetAwaiter().GetResult();
        lock (MinecraftCacheLock)
        {
            _minecraftVersions![build.Version] = result;
            try
            {
                var json = new JsonObject();
                foreach (var (version, minecraft) in _minecraftVersions) json[version] = minecraft;
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(MinecraftCachePath, json.ToJsonString());
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Horizon] 写入 Minecraft 版本缓存失败");
            }
        }

        return result;
    }

    /// <summary>实例 json 中记录的 Horizon 版本（签名版本与标题）；不是 Horizon 实例时返回 null。</summary>
    public static (string Version, string Title)? ReadInstalled(McInstance? instance)
    {
        if (!IsHorizon(instance)) return null;
        var data = instance!.JsonObject?[DataKey];
        var version = data?["version"]?.ToString();
        if (string.IsNullOrEmpty(version)) return null;
        return (version, data?["title"]?.ToString() is { Length: > 0 } title ? title : version);
    }

    public static string Dir(string instanceFolder) => Path.Combine(instanceFolder, "horizon");
    public static string BuildDir(string instanceFolder) => Path.Combine(Dir(instanceFolder), "build");
    public static string ResourcesDir(string instanceFolder) => Path.Combine(Dir(instanceFolder), "resources");
    public static string ServerJarPath(string instanceFolder) => Path.Combine(Dir(instanceFolder), "server.jar");

    public static string PinnedJsonPath(string instanceFolder, HorizonPins pins) =>
        Path.Combine(Dir(instanceFolder), pins.Version + ".json");

    /// <summary>该实例是否为 Horizon 实例（直接读 json，不依赖实例缓存）。</summary>
    public static bool IsHorizon(McInstance? instance)
    {
        if (instance is null) return false;
        try
        {
            if (!instance.IsLoaded) instance.Load();
            return instance.JsonObject?[DataKey] is not null;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Horizon] 判断实例类型失败");
            return false;
        }
    }

    #region 安装

    /// <summary>
    ///     在原版文件合并完成后执行：下载并校验 Horizon 构建、固定版本的官方 json 与服务端 jar，
    ///     并在实例 json 中写入 <c>horizon_data</c>。
    /// </summary>
    public static void Install(Loading.LoaderTask<string, string> task, string instanceFolder, string instanceName,
        string minecraftName, HorizonRemoteBuild build)
    {
        DownloadBuild(task, instanceFolder, build);
        var pins = HorizonPins.Read(BuildDir(instanceFolder));
        if (pins.Version != minecraftName)
            throw new InvalidDataException($"Horizon {build.Version} 对应 Minecraft {pins.Version}，不能装在 {minecraftName} 上。");
        EnsureGameFiles(task, instanceFolder, pins);

        _WriteData(instanceFolder, instanceName, build.Version, build.Title, pins.Version);
        LauncherCommon.Log($"[Horizon] 已安装 {build.Title} 到 {instanceFolder}");
    }

    /// <summary>
    ///     重置实例但不更换 Horizon 版本时保留已下载的构建（它的归档 Nightly 可能已被删除、无法重新下载）：
    ///     确认构建完整、固定的 Minecraft 版本相符，补齐官方 json 与服务端 jar，并重新写入 <c>horizon_data</c>。
    /// </summary>
    public static void Reattach(Loading.LoaderTask<string, string> task, string instanceFolder, string instanceName,
        string minecraftName, string version, string title)
    {
        var buildDir = BuildDir(instanceFolder);
        if (!File.Exists(Path.Combine(buildDir, HorizonProcess.ClientExe)))
            throw new FileNotFoundException($"实例中的 Horizon 构建（{title}）已缺失，请在“修改”中重新选择一个 Horizon 版本。");
        var pins = HorizonPins.Read(buildDir);
        if (pins.Version != minecraftName)
            throw new InvalidDataException($"Horizon {version} 对应 Minecraft {pins.Version}，不能装在 {minecraftName} 上。");
        EnsureGameFiles(task, instanceFolder, pins);
        _WriteData(instanceFolder, instanceName, version, title, pins.Version);
        LauncherCommon.Log($"[Horizon] 已保留 {title} 并重写实例信息：{instanceFolder}");
    }

    private static void _WriteData(string instanceFolder, string instanceName, string version, string title, string minecraft)
    {
        var jsonPath = Path.Combine(instanceFolder, instanceName + ".json");
        var json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
        json[DataKey] = new JsonObject
        {
            ["version"] = version,
            ["title"] = title,
            ["minecraft"] = minecraft
        };
        File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    ///     下载 Horizon 构建，校验 Ed25519 签名与 SHA-256 后解压到 <see cref="BuildDir" />。
    /// </summary>
    public static void DownloadBuild(Loading.LoaderBase task, string instanceFolder, HorizonRemoteBuild build)
    {
        var staging = Path.Combine(Dir(instanceFolder), ".download");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, build.AssetName);
        var sums = Path.Combine(staging, HorizonReleaseVerifier.SumsFileName);
        var signature = Path.Combine(staging, build.SignatureFileName);
        new LoaderDownload("下载 Horizon 构建", [
            new DownloadFile(build.SumsUrls, sums),
            new DownloadFile(build.SignatureUrls, signature),
            new DownloadFile(build.AssetUrls, archive,
                new FileChecker(actualSize: build.AssetSize > 0 ? build.AssetSize : -1, canUseExistsFile: false))
        ]).WaitForExit(loaderToSyncProgress: task);

        var digest = HorizonReleaseVerifier.SignedDigest(
            build.Version, File.ReadAllBytes(sums), File.ReadAllText(signature), build.AssetName);
        var actual = HorizonReleaseVerifier.Sha256OfFile(archive);
        if (!string.Equals(digest, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载的 {build.AssetName} 与 Horizon 签名清单不符，已停止安装，请重试。");
        LauncherCommon.Log($"[Horizon] 构建 {build.Version} 的签名与 SHA-256 校验通过");

        var extract = Path.Combine(staging, "extract");
        ZipFile.ExtractToDirectory(archive, extract);
        // 压缩包内有一层顶级文件夹
        var content = File.Exists(Path.Combine(extract, HorizonProcess.ClientExe))
            ? extract
            : Directory.GetDirectories(extract).FirstOrDefault(dir => File.Exists(Path.Combine(dir, HorizonProcess.ClientExe)))
              ?? throw new InvalidDataException("Horizon 构建中没有 horizon-client.exe");
        var target = BuildDir(instanceFolder);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(content, target);
        Directory.Delete(staging, true);
    }

    /// <summary>
    ///     确保固定版本的官方 json 与服务端 jar 存在且校验通过（BMCLAPI 镜像优先）。
    /// </summary>
    public static void EnsureGameFiles(Loading.LoaderBase task, string instanceFolder, HorizonPins pins)
    {
        var jsonPath = PinnedJsonPath(instanceFolder, pins);
        if (!pins.IsAcceptedManifest(jsonPath))
            new LoaderDownload("下载 Minecraft 版本文件", [
                new DownloadFile(HorizonGameFiles.ManifestUrls(pins), jsonPath,
                    new FileChecker(hash: pins.ManifestSha1s[0], canUseExistsFile: false, isJson: true))
            ]).WaitForExit(loaderToSyncProgress: task);

        var serverJar = ServerJarPath(instanceFolder);
        if (!File.Exists(serverJar) ||
            !string.Equals(HorizonGameFiles.Sha1OfFile(serverJar), pins.ServerSha1, StringComparison.OrdinalIgnoreCase))
            new LoaderDownload("下载 Minecraft 服务端", [
                new DownloadFile(HorizonGameFiles.ServerJarUrls(pins, jsonPath), serverJar,
                    new FileChecker(hash: pins.ServerSha1, canUseExistsFile: false))
            ]).WaitForExit(loaderToSyncProgress: task);
    }

    #endregion

    #region 准备资源

    /// <summary>
    ///     启动前检查资源，缺失或过期时用实例自己的原版文件运行 horizon-prepare。
    /// </summary>
    public static void Prepare(Loading.LoaderTask<int, int> task, McInstance instance, string javaHome)
    {
        var folder = instance.PathInstance.TrimEnd('\\');
        var buildDir = BuildDir(folder);
        if (!File.Exists(Path.Combine(buildDir, HorizonProcess.ClientExe)))
            throw new FileNotFoundException("Horizon 构建文件缺失，请重新安装这个 Horizon 实例。");
        var pins = HorizonPins.Read(buildDir);
        EnsureGameFiles(task, folder, pins);

        var resources = ResourcesDir(folder);
        Directory.CreateDirectory(resources);
        var gaps = HorizonProcess.CheckPreparedAsync(buildDir, resources, task.AbortedToken).GetAwaiter().GetResult();
        if (gaps is null)
        {
            LauncherCommon.Log("[Horizon] 资源已是最新，跳过准备");
            return;
        }

        LauncherCommon.Log($"[Horizon] 需要准备资源：{gaps}");
        var clientJar = Path.Combine(folder, instance.Name + ".jar");
        if (!File.Exists(clientJar) ||
            !string.Equals(HorizonGameFiles.Sha1OfFile(clientJar), pins.ClientSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"实例的 {instance.Name}.jar 不是 Minecraft {pins.Version} 原版客户端，请在实例设置中补全文件后再试。");
        var minecraftFolder = Path.GetFullPath(Path.Combine(folder, "..", ".."));

        var api = HorizonProcess.QueryPrepareApiAsync(buildDir, task.AbortedToken).GetAwaiter().GetResult();
        var arguments = new List<string> { "--prepare", "--output", resources, "--complete", "--progress", "json" };

        void Add(string option, string value)
        {
            if (!api.Options.Contains(option))
                throw new NotSupportedException($"这个 Horizon 构建不支持 {option}，请重新安装最新的 Horizon。");
            arguments.Add(option);
            arguments.Add(value);
        }

        Add("--server-jar", ServerJarPath(folder));
        Add("--version-json", PinnedJsonPath(folder, pins));
        Add("--client-jar", clientJar);
        Add("--libraries-dir", Path.Combine(minecraftFolder, "libraries"));
        Add("--assets-dir", Path.Combine(minecraftFolder, "assets"));
        Add("--java-home", javaHome);

        HorizonProcess.RunPrepareAsync(buildDir, arguments, javaHome,
                message => LauncherCommon.Log("[Horizon] " + message),
                fraction => task.Progress = fraction,
                task.AbortedToken)
            .GetAwaiter().GetResult();
        LauncherCommon.Log("[Horizon] 资源准备完成");
    }

    #endregion
}
