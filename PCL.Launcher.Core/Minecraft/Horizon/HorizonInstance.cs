using System.IO.Compression;
using System.Net.Http;
using PCL.Core.Minecraft.Horizon;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     安装页可选的 Horizon 构建：最新 Nightly 及其固定的 Minecraft 版本。
/// </summary>
public sealed record HorizonCandidate(HorizonRemoteBuild Build, string Minecraft);

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
    ///     最新 Horizon Nightly 及其 Minecraft 版本，供安装页使用。
    /// </summary>
    public static readonly Loading.LoaderTask<int, HorizonCandidate> DlHorizonListLoader =
        new("DlHorizonList Main", loader =>
        {
            var build = HorizonRelease.GetLatestNightlyAsync(Http, loader.AbortedToken).GetAwaiter().GetResult();
            var minecraft = HorizonRelease.GetMinecraftVersionAsync(Http, build, loader.AbortedToken).GetAwaiter().GetResult();
            LauncherCommon.Log($"[Horizon] 最新构建：{build.Title}（Minecraft {minecraft}）");
            loader.output = new HorizonCandidate(build, minecraft);
        });

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

        var jsonPath = Path.Combine(instanceFolder, instanceName + ".json");
        var json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
        json[DataKey] = new JsonObject
        {
            ["version"] = build.Version,
            ["title"] = build.Title,
            ["minecraft"] = pins.Version
        };
        File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        LauncherCommon.Log($"[Horizon] 已安装 {build.Title} 到 {instanceFolder}");
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
        var signature = Path.Combine(staging, HorizonReleaseVerifier.NightlySignatureFileName);
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
