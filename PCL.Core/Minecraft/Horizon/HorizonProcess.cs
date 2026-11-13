using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     <c>horizon-prepare --launch-api</c> 的回复中本启动器用到的部分。
/// </summary>
public sealed record HorizonPrepareApi(string LaunchApi, string Horizon, string Minecraft, int JavaMajor, IReadOnlySet<string> Options);

/// <summary>
///     调用 Horizon 构建里的 <c>horizon-prepare</c> 与 <c>horizon-client</c>，
///     遵循 Horizon 的 Launch API（docs/api/launch.md）：只传 <c>--launch-api</c> 列出的选项，
///     准备进度读 <c>--progress json</c> 的逐行 JSON，会话走 <c>--session-stdin</c>。
/// </summary>
public static class HorizonProcess
{
    public const string PrepareExe = "horizon-prepare.exe";
    public const string ClientExe = "horizon-client.exe";

    /// <summary>
    ///     读取 <c>horizon-prepare --launch-api</c>。构建过旧（不支持 Launch API）时抛出异常。
    /// </summary>
    public static async Task<HorizonPrepareApi> QueryPrepareApiAsync(string buildDir, CancellationToken token = default)
    {
        var (exitCode, stdout, stderr) = await _RunToEndAsync(Path.Combine(buildDir, PrepareExe), ["--launch-api"], buildDir, token);
        if (exitCode != 0)
            throw new InvalidOperationException($"这个 Horizon 构建不支持 Launch API（horizon-prepare 退出码 {exitCode}）：{_LastLine(stderr)}");
        var json = JsonNode.Parse(stdout)?.AsObject()
                   ?? throw new InvalidDataException("horizon-prepare --launch-api 没有输出 JSON");
        var options = (json["options"]?.AsArray() ?? [])
            .Select(option => option?["name"]?.GetValue<string>())
            .OfType<string>()
            .ToHashSet();
        return new HorizonPrepareApi(
            json["launch_api"]?.GetValue<string>() ?? "",
            json["horizon"]?.GetValue<string>() ?? "",
            json["minecraft"]?.GetValue<string>() ?? throw new InvalidDataException("--launch-api 缺少 minecraft"),
            json["java_major"]?.GetValue<int>() ?? throw new InvalidDataException("--launch-api 缺少 java_major"),
            options);
    }

    /// <summary>
    ///     读取 <c>horizon-client --launch-api</c> 列出的选项名。
    /// </summary>
    public static async Task<IReadOnlySet<string>> QueryClientOptionsAsync(string buildDir, CancellationToken token = default)
    {
        var (exitCode, stdout, stderr) = await _RunToEndAsync(Path.Combine(buildDir, ClientExe), ["--launch-api"], buildDir, token);
        if (exitCode != 0)
            throw new InvalidOperationException($"这个 Horizon 构建不支持 Launch API（horizon-client 退出码 {exitCode}）：{_LastLine(stderr)}");
        var json = JsonNode.Parse(stdout)?.AsObject()
                   ?? throw new InvalidDataException("horizon-client --launch-api 没有输出 JSON");
        return (json["options"]?.AsArray() ?? [])
            .Select(option => option?["name"]?.GetValue<string>())
            .OfType<string>()
            .ToHashSet();
    }

    /// <summary>
    ///     <c>horizon-prepare --check --output PREPARED</c>：资源完整且与该构建匹配时返回 null，否则返回缺口说明。
    /// </summary>
    public static async Task<string?> CheckPreparedAsync(string buildDir, string preparedDir, CancellationToken token = default)
    {
        var (exitCode, stdout, stderr) = await _RunToEndAsync(
            Path.Combine(buildDir, PrepareExe), ["--check", "--output", preparedDir], buildDir, token);
        if (exitCode == 0) return null;
        var detail = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        return string.IsNullOrWhiteSpace(detail) ? $"horizon-prepare --check 退出码 {exitCode}" : detail.Trim();
    }

    /// <summary>
    ///     运行 <c>horizon-prepare</c> 进行准备。<paramref name="arguments" /> 中应含
    ///     <c>--prepare</c>、<c>--output</c>、<c>--progress json</c> 等。
    ///     每行进度 JSON 分别交给 <paramref name="onMessage" /> 与 <paramref name="onFraction" />；失败时抛出异常。
    /// </summary>
    public static async Task RunPrepareAsync(
        string buildDir,
        IReadOnlyList<string> arguments,
        string? javaHome,
        Action<string> onMessage,
        Action<double> onFraction,
        CancellationToken token = default)
    {
        var info = _StartInfo(Path.Combine(buildDir, PrepareExe), arguments, buildDir);
        if (!string.IsNullOrEmpty(javaHome))
        {
            info.Environment["JAVA_HOME"] = javaHome;
            info.Environment["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator +
                                       (Environment.GetEnvironmentVariable("PATH") ?? "");
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        string? error = null;
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(token) is { } line)
            {
                stderr.AppendLine(line);
                onMessage(line);
            }
        }, token);

        await using (token.Register(() => _TryKill(process)))
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                JsonObject? json;
                try
                {
                    json = JsonNode.Parse(line)?.AsObject();
                }
                catch (Exception)
                {
                    json = null;
                }

                if (json is null)
                {
                    onMessage(line);
                    continue;
                }

                if (json["message"]?.GetValue<string>() is { } message) onMessage(message);
                if (json["fraction"] is { } fraction) onFraction(Math.Clamp(fraction.GetValue<double>(), 0, 1));
                if (json["error"]?.GetValue<string>() is { } failure) error = failure;
            }

            await process.WaitForExitAsync(token);
            await stderrTask;
        }

        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error ?? $"horizon-prepare 失败（退出码 {process.ExitCode}）：{_LastLine(stderr.ToString())}");
    }

    /// <summary>
    ///     启动 <c>horizon-client</c>。<paramref name="sessionJson" /> 不为空时写入其标准输入后关闭，
    ///     调用方需已在 <paramref name="arguments" /> 中加入 <c>--session-stdin</c>。
    ///     返回已启动的进程，标准错误已重定向，调用方负责读取。
    /// </summary>
    public static Process StartClient(string buildDir, IReadOnlyList<string> arguments, string workingDir, string? sessionJson)
    {
        var info = _StartInfo(Path.Combine(buildDir, ClientExe), arguments, workingDir);
        info.RedirectStandardOutput = false;
        info.RedirectStandardInput = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 horizon-client");
        try
        {
            if (sessionJson is not null) process.StandardInput.Write(sessionJson);
        }
        finally
        {
            process.StandardInput.Close();
        }

        return process;
    }

    /// <summary>
    ///     Horizon 的会话 JSON（docs/api/launch.md#the-session-json）。
    /// </summary>
    public static string SessionJson(string name, string uuid, string accessToken, string? xuid)
    {
        var json = new JsonObject
        {
            ["name"] = name,
            ["uuid"] = uuid.Replace("-", ""),
            ["access_token"] = accessToken,
            ["user_type"] = "msa"
        };
        if (!string.IsNullOrEmpty(xuid)) json["xuid"] = xuid;
        return json.ToJsonString();
    }

    private static ProcessStartInfo _StartInfo(string exe, IEnumerable<string> arguments, string workingDir)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("Horizon 构建不完整，缺少文件", exe);
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> _RunToEndAsync(
        string exe, IEnumerable<string> arguments, string workingDir, CancellationToken token)
    {
        using var process = new Process { StartInfo = _StartInfo(exe, arguments, workingDir) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await using (token.Register(() => _TryKill(process)))
        {
            await process.WaitForExitAsync(token);
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static void _TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (Exception)
        {
            // 进程已退出
        }
    }

    internal static string _LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
