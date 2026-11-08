using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Logging;
using PCL.Core.Platform;
using PCL.Core.Utils;
using PCL.Core.Utils.Codecs;

namespace PCL;

/// <summary>启动器业务层日志级别，与原 <c>Common.LogLevel</c> 数值保持一致。</summary>
public enum LauncherLogLevel
{
    Normal = 0,
    Developer = 1,
    Debug = 2,
    Hint = 3,
    Msgbox = 4,
    Feedback = 5,
    Critical = 6
}

/// <summary>
///     业务层通用工具。UI 提示通过 <see cref="UiLogHandler" /> 交给外层，
///     其余日志直接写入 <see cref="LogWrapper" />。
/// </summary>
public static class LauncherCommon
{
    /// <summary>UI 日志处理器；由 WPF 宿主在启动时注入。</summary>
    public delegate void LauncherLogHandler(
        Exception? ex,
        string text,
        LauncherLogLevel level,
        string? title,
        string? userSummary);

    /// <summary>UTF-8 且不写 BOM 的编码实例。写任何 Minecraft 会读取的文本文件都必须用它。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly Lock WriteIniLock = new();

    public static LauncherLogHandler? UiLogHandler { get; set; }

    /// <summary>是否处于调试模式，由宿主同步。</summary>
    public static bool ModeDebug { get; set; }

    public static string VersionStandardCode => Basics.Metadata.Version.StandardVersion;

    public static string UpstreamVersion => Basics.Metadata.Version.UpstreamVersion;
    public static string VersionBaseName => Basics.VersionName;
    public static int VersionCode => Basics.VersionCode;

#if DEBUG
    public const string VersionBranchCode = "100";
#elif DEBUGCI
    public const string VersionBranchCode = "50";
#else
    public const string VersionBranchCode = "0";
#endif

    public static void Log(
        string text,
        LauncherLogLevel level = LauncherLogLevel.Normal,
        string? title = null,
        string? userSummary = null)
    {
        switch (level)
        {
            case >= LauncherLogLevel.Hint when UiLogHandler is not null:
                UiLogHandler(null, text, level, title, userSummary);
                break;

            case LauncherLogLevel.Debug or LauncherLogLevel.Developer:
                LogWrapper.Debug("Launcher", text);
                break;

            default:
                LogWrapper.Info("Launcher", text);
                break;
        }
    }

    public static void Log(
        Exception ex,
        string desc,
        LauncherLogLevel level = LauncherLogLevel.Debug,
        string? title = null,
        string? userSummary = null)
    {
        if (ex is ThreadInterruptedException) return;

        switch (level)
        {
            case >= LauncherLogLevel.Hint when UiLogHandler is not null:
                UiLogHandler(ex, desc, level, title, userSummary);
                break;

            case >= LauncherLogLevel.Feedback:
                LogWrapper.Error(ex, "Launcher", desc);
                break;

            case LauncherLogLevel.Hint or LauncherLogLevel.Msgbox:
                LogWrapper.Warn(ex, "Launcher", desc);
                break;

            default:
                LogWrapper.Debug(ex, "Launcher", desc);
                break;
        }
    }

    private static int _uuid;

    /// <summary>获取一个全程序内不会重复的数字（伪 Uuid）。</summary>
    public static int GetUuid()
    {
        return Interlocked.Increment(ref _uuid);
    }

    public static bool RunInUi()
    {
        return PlatformServices.UiDispatcher?.CheckAccess() ?? false;
    }

    /// <summary>确保在 UI 线程中执行代码，不阻断当前线程。</summary>
    public static void RunInUi(Action action, bool forceWaitUntilLoaded = false)
    {
        var dispatcher = PlatformServices.UiDispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Post(action);
    }

    /// <summary>确保在 UI 线程中执行代码并返回结果。</summary>
    public static T? RunInUiWait<T>(Func<T> func)
    {
        var dispatcher = PlatformServices.UiDispatcher;
        if (dispatcher is null) return default;
        return dispatcher.CheckAccess() ? func() : dispatcher.Invoke(func);
    }

    /// <summary>确保在 UI 线程中执行代码，必要时阻断当前线程直至执行完毕。</summary>
    public static void RunInUiWait(Action action)
    {
        var dispatcher = PlatformServices.UiDispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    /// <summary>确保在工作线程中执行代码。</summary>
    public static void RunInThread(Action action)
    {
        if (RunInUi()) Task.Run(action);
        else action();
    }

    /// <summary>在新线程中执行代码；线程内异常不会终止进程。</summary>
    public static Thread RunInNewThread(
        Action action,
        string name = "Launcher",
        ThreadPriority priority = ThreadPriority.Normal)
    {
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (ThreadInterruptedException)
            {
                Log($"{name}：线程已中止");
            }
            catch (Exception ex)
            {
                Log(ex, $"{name}：线程执行失败", LauncherLogLevel.Feedback);
            }
        })
        {
            IsBackground = true,
            Name = name,
            Priority = priority
        };
        thread.Start();
        return thread;
    }

    /// <summary>获取打包资源流；资源不存在时返回 <c>null</c>。</summary>
    public static Stream? GetResourceStream(string path)
    {
        return PlatformServices.ResourceStore?.GetResourceStream(path);
    }

    /// <summary>创建文件或目录符号链接。</summary>
    public static void CreateSymbolicLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(targetPath)) Directory.CreateSymbolicLink(linkPath, targetPath);
        else File.CreateSymbolicLink(linkPath, targetPath);
    }

    public static string ReadIni(string fileName, string key, string defaultValue = "")
    {
        try
        {
            if (!File.Exists(fileName)) return defaultValue;
            foreach (var line in File.ReadAllLines(fileName))
            {
                var index = line.IndexOf(':');
                if (index > 0 && line[..index] == key)
                    return line[(index + 1)..];
            }
        }
        catch
        {
            // ignored
        }

        return defaultValue;
    }

    /// <summary>
    ///     以 UTF-8（<b>无 BOM</b>）写入 ini 文件的单个键；值未变化时不落盘，<paramref name="value" /> 为 <c>null</c> 时删除该键。
    /// </summary>
    /// <remarks>
    ///     严禁改回 <see cref="Encoding.UTF8" />：该编码写入时会带 BOM，而 Minecraft 的
    ///     <c>net.minecraft.client.Options#load</c> 使用不剥离 BOM 的 UTF-8 Reader 逐行解析，
    ///     首行键名会被污染成 <c>"\uFEFFversion"</c>，导致版本号读不到而按 0 处理，
    ///     整套 DataFixer 从最老版本重跑（<c>OptionsKeyLwjgl3Fix</c> 会把已是字符串形式的
    ///     <c>key_key.*</c> 值交给 <c>Integer.parseInt</c> 并抛异常），最终整份设置被判为无效、
    ///     回落到默认值并被覆盖保存——表现为「每次启动游戏设置都被重置」。
    /// </remarks>
    public static void WriteIni(string fileName, string key, string? value)
    {
        try
        {
            if (key.Contains(":"))
                throw new Exception($"尝试写入 ini 文件 {fileName} 的键名中包含了冒号：{key}");
            key = key.Replace("\r", "").Replace("\n", "");
            value = value?.Replace("\r", "").Replace("\n", "");

            lock (WriteIniLock)
            {
                var content = new Dictionary<string, string>(StringComparer.Ordinal);
                var hasBom = false;
                if (File.Exists(fileName))
                {
                    var rawBytes = File.ReadAllBytes(fileName);
                    hasBom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
                    // File.ReadAllLines 会自动剥离 BOM，因此这里的键名是干净的
                    foreach (var line in File.ReadAllLines(fileName))
                    {
                        var index = line.IndexOf(":");
                        if (index > 0) content[line[..index]] = line[(index + 1)..];
                    }
                }

                if (value is null)
                {
                    if (!content.Remove(key) && !hasBom) return;
                }
                else
                {
                    // 值未变化则不落盘：避免无谓的重写整个文件、也避免改动文件时间戳
                    // 例外：文件仍带 BOM 时必须重写一次，否则历史遗留的 BOM 永远不会被清掉，MC 会继续读到污染的首行
                    if (!hasBom && content.TryGetValue(key, out var oldValue) && oldValue == value) return;
                    content[key] = value;
                }

                WriteFile(
                    fileName,
                    string.Join("\r\n", content.Select(pair => $"{pair.Key}:{pair.Value}")) + "\r\n",
                    Utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            Log(ex, $"写入 ini 文件失败（{fileName} → {key}:{value}）", LauncherLogLevel.Debug);
        }
    }

    public static int GetHash(string text)
    {
        return BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0)
            & int.MaxValue;
    }

    public static string ShellAndGetOutput(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>删除目录（递归）。</summary>
    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    /// <summary>复制文件，自动创建目标目录并覆盖已有文件。</summary>
    public static void CopyFile(string fromPath, string toPath)
    {
        var directory = Path.GetDirectoryName(toPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.Copy(fromPath, toPath, true);
    }

    public static string GetFileMd5(string filePath)
    {
        return GetFileHash(filePath, MD5.Create());
    }

    public static string GetFileSha1(string filePath)
    {
        return GetFileHash(filePath, SHA1.Create());
    }

    public static string GetFileSha256(string filePath)
    {
        return GetFileHash(filePath, SHA256.Create());
    }

    private static string GetFileHash(string filePath, HashAlgorithm algorithm)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    public static IEnumerable<FileInfo> EnumerateFiles(string path)
    {
        return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories);
    }

    public static int Dpi { get; set; } = 96;

    public static string VersionBranchName => "Release";

    public enum ProcessReturnValues
    {
        Aborted = -1,
        Success = 0,
        Fail = 1,
        Exception = 2,
        Timeout = 3,
        Cancel = 4,
        TaskDone = 5
    }

    public static double GetPixelSize(double wpfSize)
    {
        return wpfSize / 96d * Dpi;
    }

    public static bool IsUtf8CodePage()
    {
        return Encoding.Default.CodePage == 65001;
    }

    public static bool IsValidInstanceName(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0
            && name.Length <= 64;
    }

    public static void CopyDirectory(
        string source,
        string destination,
        Action<double>? progress = null)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var done = 0;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            done += 1;
            if (files.Length > 0) progress?.Invoke((double)done / files.Length);
        }
    }

    public static void ShellOnly(string fileName, string arguments)
    {
        Process.Start(
            new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
    }

    public static void IniClearCache()
    {
    }

    public static void IniClearCache(string path)
    {
    }

    public static string GetTempFolder(string prefix)
    {
        var path = Path.Combine(LauncherPaths.Temp, prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ShortenPath(string longPath, int shortenThreshold = 247)
    {
        if (longPath.Length <= shortenThreshold) return longPath;

        var shortPath = new StringBuilder(260);
        _ = GetShortPathName(longPath, shortPath, shortPath.Capacity);
        var result = shortPath.ToString();
        return string.IsNullOrEmpty(result) ? longPath : result;
    }

    [DllImport("kernel32", EntryPoint = "GetShortPathNameA", CharSet = CharSet.Ansi)]
    private static extern int GetShortPathName(
        string lpszLongPath,
        StringBuilder lpszShortPath,
        int cchBuffer);

    public static string? GetStringFromEnum(Enum enumData)
    {
        return Enum.GetName(enumData.GetType(), enumData);
    }

    /// <summary>将文件大小转化为适合的文本形式，如“1.28 M”。</summary>
    public static string GetString(long fileSize)
    {
        return ByteStream.GetReadableLength(fileSize, provider: Lang.Culture);
    }

    public static string ReadFile(Stream stream, Encoding? encoding = null)
    {
        using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public static string ReadFile(string filePath, Encoding? encoding = null)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return (encoding ?? EncodingDetector.DetectEncoding(bytes)).GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public static byte[] ReadFileBytes(string filePath)
    {
        try
        {
            return File.ReadAllBytes(filePath);
        }
        catch
        {
            return [];
        }
    }

    public static void ExtractFile(
        string compressFilePath,
        string destDirectory,
        Encoding? encoding = null,
        Action<double>? progressIncrementHandler = null)
    {
        Directory.CreateDirectory(destDirectory);
        var fullDestDirectory = Path.GetFullPath(destDirectory);
        if (!fullDestDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()))
            fullDestDirectory += Path.DirectorySeparatorChar;

        if (compressFilePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var compressedFile = new FileStream(compressFilePath, FileMode.Open, FileAccess.Read);
            using var decompressStream = new GZipStream(compressedFile, CompressionMode.Decompress);
            var fileName = Path.GetFileName(compressFilePath);
            if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^3];
            if (fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^4];
            using var extractFileStream = new FileStream(
                Path.Combine(fullDestDirectory, fileName.ToLowerInvariant()),
                FileMode.OpenOrCreate,
                FileAccess.Write);
            decompressStream.CopyTo(extractFileStream);
            progressIncrementHandler?.Invoke(1d);
            return;
        }

        using var archive = ZipFile.Open(
            compressFilePath,
            ZipArchiveMode.Read,
            encoding ?? Encoding.GetEncoding("GB18030"));
        var total = archive.Entries.Count;
        foreach (var entry in archive.Entries)
        {
            if (total > 0) progressIncrementHandler?.Invoke(1d / total);

            var path = Path.GetFullPath(Path.Combine(fullDestDirectory, entry.FullName));
            if (!path.StartsWith(fullDestDirectory, StringComparison.OrdinalIgnoreCase))
                throw new Exception(
                    $"解压文件 {entry.FullName} 错误：解压路径 {path} 不在目标目录 {fullDestDirectory} 内");

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            entry.ExtractToFile(path, true);
        }
    }

    public static string GetHexString(Memory<byte> bytes)
    {
        return Convert.ToHexString(bytes.Span).ToLowerInvariant();
    }

    public static string GetStringMd5(string text)
    {
        return Convert
            .ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
    }

    public static string Base64Encode(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    public static string Base64Decode(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? ""
            : Encoding.UTF8.GetString(Convert.FromBase64String(text));
    }

    public static void WriteFile(string filePath, byte[] content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(filePath, content);
    }

    public static void WriteFile(string filePath, string content, Encoding? encoding = null)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // 默认使用不带 BOM 的 UTF-8：带 BOM 的文本会让不剥离 BOM 的读取方（如 Minecraft）解析出错
        File.WriteAllText(filePath, content, encoding ?? Utf8NoBom);
    }

    public static void WriteFile(string filePath, Stream stream)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var output = File.Create(filePath);
        stream.CopyTo(output);
    }

    public static JsonNode GetJson(string data)
    {
        try
        {
            return JsonCompat.ParseNode(data);
        }
        catch (Exception ex)
        {
            var dataText = data ?? "";
            var length = dataText.Length;
            throw new Exception(
                $"格式化 JSON 失败：{(length > 2000
                    ? $"{dataText[..500]}...(全长 {length} 个字符)...{dataText[(length - 500)..]}"
                    : dataText)}",
                ex);
        }
    }

    public static string GetPathFromFullPath(string filePath)
    {
        if (!(filePath.Contains('\\') || filePath.Contains('/')))
            throw new Exception($"不包含路径：{filePath}");

        if (filePath.EndsWith('\\') || filePath.EndsWith('/'))
        {
            var isRight = filePath.EndsWith('\\');
            filePath = filePath[..^1];
            return filePath[..filePath.LastIndexOfAny(['\\', '/'])] + (isRight ? "\\" : "/");
        }

        var result = filePath[..(filePath.LastIndexOfAny(['\\', '/']) + 1)];
        return string.IsNullOrEmpty(result)
            ? throw new Exception($"不包含路径：{filePath}")
            : result;
    }

    public static string GetFileNameFromPath(string filePath)
    {
        filePath = filePath.Replace("/", "\\");
        if (filePath.EndsWith('\\')) throw new Exception($"不包含文件名：{filePath}");
        if (filePath.Contains('?')) filePath = filePath[..filePath.IndexOf('?')];
        if (filePath.Contains('\\')) filePath = filePath[(filePath.LastIndexOf('\\') + 1)..];

        return filePath.Length switch
        {
            0 => throw new Exception($"不包含文件名：{filePath}"),
            > 250 => throw new PathTooLongException($"文件名过长：{filePath}"),
            _ => filePath
        };
    }

    public static string GetFolderNameFromPath(string folderPath)
    {
        if (folderPath.EndsWith(":\\") || folderPath.EndsWith(":\\\\"))
            return folderPath[..1];
        if (folderPath.EndsWith('\\') || folderPath.EndsWith('/'))
            folderPath = folderPath[..^1];
        return GetFileNameFromPath(folderPath);
    }

    public static void CheckPermissionWithException(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException(nameof(path), "文件夹名不能为空！");
        if (!path.EndsWith('\\')) path += "\\";
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("文件夹不存在！");
        if (File.Exists($"{path}CheckPermission"))
            File.Delete($"{path}CheckPermission");
        File.Create($"{path}CheckPermission").Dispose();
        File.Delete($"{path}CheckPermission");
    }
}