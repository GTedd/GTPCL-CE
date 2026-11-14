using System.Diagnostics;
using System.Text;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Horizon;

namespace PCL;

/// <summary>
///     Horizon 实例的启动：前面的 Java、登录、补全文件与普通实例相同，
///     之后用 horizon-prepare 准备资源，再启动 horizon-client 代替 Java 进程（Horizon Launch API）。
/// </summary>
public static partial class GameLaunch
{
    private static List<Loading.LoaderBase> _CreateHorizonProcessLoaders()
    {
        return
        [
            new Loading.LoaderTask<int, int>("准备 Horizon 资源", _HorizonPrepare) { ProgressWeight = 8d, block = true },
            new Loading.LoaderTask<int, Process>("启动 Horizon", _HorizonRun) { ProgressWeight = 1d },
            new Loading.LoaderTask<Process, int>("等待 Horizon 窗口出现", _HorizonWait) { ProgressWeight = 2d },
            new Loading.LoaderTask<int, int>(Lang.Text("Minecraft.Launch.Stage.End"), _ => McLaunchEnd())
                { ProgressWeight = 1d }
        ];
    }

    private static void _HorizonPrepare(Loading.LoaderTask<int, int> task)
    {
        var instance = InstanceList.McMcInstanceSelected!;
        var pins = HorizonPins.Read(HorizonInstance.BuildDir(instance.PathInstance.TrimEnd('\\')));
        var java = mcLaunchJavaSelected;
        if (java is null || java.Installation.MajorVersion != pins.JavaMajor)
        {
            lock (JavaRuntime.JavaLock)
            {
                java = JavaRuntime.JavaSelect("$$", new Version(pins.JavaMajor, 0), new Version(pins.JavaMajor, 999),
                    instance, true, new HashSet<int> { pins.JavaMajor });
            }
        }

        if (java is null)
            throw new InvalidOperationException($"Horizon 准备资源需要 Java {pins.JavaMajor}，请先在设置中添加或下载 Java {pins.JavaMajor}。");
        McLaunchLog($"Horizon 使用 Java：{java}");
        // JavaFolder 是 bin 目录，Java 主目录是它的上一级
        var javaHome = Path.GetDirectoryName(java.Installation.JavaFolder.TrimEnd('\\', '/'))!;
        HorizonInstance.Prepare(task, instance, javaHome);
    }

    private static string _horizonLastError = "";

    private static void _HorizonRun(Loading.LoaderTask<int, Process> loader)
    {
        var instance = InstanceList.McMcInstanceSelected!;
        // 打开的原生模组已编进原生构建时启动它，否则启动 Nightly 原版构建
        var native = HorizonNativeBuild.GetStatus(instance);
        var buildDir = HorizonNativeBuild.PlayedBuildDir(instance);
        if (native.State is HorizonNativeState.NotBuilt or HorizonNativeState.NeedsRebuild)
        {
            McLaunchLog($"Horizon 原生模组状态：{native.State}，本次启动不含未编译的原生模组");
            LauncherUi.Interaction?.ShowHint(
                "有原生模组还没编译进游戏，本次启动不包含它们：请在实例的模组页点击“构建”。", NotificationType.Info);
        }
        McLaunchLog($"Horizon 构建：{buildDir}");
        var gameDir = instance.PathIndie.TrimEnd('\\');
        Directory.CreateDirectory(gameDir);
        var options = HorizonProcess.QueryClientOptionsAsync(buildDir, loader.AbortedToken).GetAwaiter().GetResult();

        var arguments = new List<string>
        {
            "--resources", HorizonInstance.ResourcesDir(instance.PathInstance.TrimEnd('\\')),
            "--game-dir", gameDir,
            "--home", gameDir
        };
        // 微软账号把会话交给游戏；其余账号不传会话，Horizon 以离线身份运行
        var login = mcLoginLoader.output;
        string? session = null;
        if (login.Type == "Microsoft" && !string.IsNullOrEmpty(login.AccessToken) && options.Contains("--session-stdin"))
        {
            arguments.Add("--session-stdin");
            session = HorizonProcess.SessionJson(login.Name, login.Uuid, login.AccessToken, _HorizonXuid(login.AccessToken));
        }

        var info = new ProcessStartInfo(Path.Combine(buildDir, HorizonProcess.ClientExe))
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // 玩家名：微软账号由会话决定，离线/外置账号用启动器里选定的用户名（否则 Horizon 会自己生成 PlayerXXXXX）
        if (!string.IsNullOrWhiteSpace(login.Name))
            info.Environment["HORIZON_PLAYER_NAME"] = login.Name;
        McLaunchLog($"启动 Horizon：{info.FileName} {string.Join(' ', arguments)}（玩家名 {login.Name}）");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _horizonLastError = "";
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) LauncherCommon.Log("[Horizon] " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _horizonLastError = e.Data;
            LauncherCommon.Log("[Horizon] " + e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (session is not null) process.StandardInput.Write(session);
        }
        finally
        {
            process.StandardInput.Close();
        }

        mcLaunchProcess = process;
        loader.output = process;
    }

    private static void _HorizonWait(Loading.LoaderTask<Process, int> loader)
    {
        var process = loader.input!;
        var deadline = DateTime.Now.AddMinutes(2);
        while (!process.HasExited && DateTime.Now < deadline && !loader.IsAborted)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) break;
            Thread.Sleep(200);
        }

        if (process.HasExited && process.ExitCode != 0)
            throw new Exception($"Horizon 启动失败（退出码 {process.ExitCode}）：{_horizonLastError}");
        McLaunchLog("Horizon 已启动");
        GameWatcher.ExternalGameStarted(process, _ =>
        {
            if (process.ExitCode is 0) return;
            LauncherUi.Interaction?.ShowHint(
                $"Horizon 异常退出（{process.ExitCode}）：{_horizonLastError}", NotificationType.Error);
        });
    }

    /// <summary>
    ///     Minecraft 访问令牌是 JWT，其中带有 xuid；读不到时返回 null（会话中 xuid 可选）。
    /// </summary>
    private static string? _HorizonXuid(string accessToken)
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
}
