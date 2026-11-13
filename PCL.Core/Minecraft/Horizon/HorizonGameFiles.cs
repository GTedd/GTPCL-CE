using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     Horizon 构建在 <c>research-lock.json</c> 中固定的 Minecraft 文件。
/// </summary>
public sealed record HorizonPins(
    string Version,
    string ClientSha1,
    string ServerSha1,
    IReadOnlyList<string> ManifestSha1s,
    int JavaMajor)
{
    public static HorizonPins Read(string buildDir)
    {
        var path = Path.Combine(buildDir, "research-lock.json");
        var json = JsonNode.Parse(File.ReadAllText(path))?["minecraft"]
                   ?? throw new InvalidDataException("research-lock.json 缺少 minecraft");
        string Text(string key) => json[key]?.GetValue<string>() ?? throw new InvalidDataException($"research-lock.json 缺少 {key}");
        var manifests = new List<string> { Text("manifest_sha1") };
        manifests.AddRange((json["accepted_manifest_sha1"]?.AsArray() ?? [])
            .Select(node => node?.GetValue<string>())
            .OfType<string>());
        return new HorizonPins(
            Text("version"),
            Text("client_sha1"),
            Text("server_sha1"),
            manifests,
            json["java_major"]?.GetValue<int>() ?? 25);
    }

    public bool IsAcceptedManifest(string path) =>
        File.Exists(path) && ManifestSha1s.Contains(HorizonGameFiles.Sha1OfFile(path));
}

/// <summary>
///     在某个 .minecraft 中找到的、与固定版本一致的客户端文件。
///     <see cref="VersionJson" /> 为 null 表示本地的版本 JSON 被启动器改写过，需要另取官方原版。
/// </summary>
public sealed record HorizonLocalClient(
    string MinecraftDir,
    string ClientJar,
    string? VersionJson,
    string LibrariesDir,
    string AssetsDir);

/// <summary>
///     按内容（SHA-1）而不是按版本文件夹名识别本机已有的 Minecraft 客户端，
///     因此改过名、开了版本隔离或装了加载器的实例都能被识别。
/// </summary>
public static class HorizonGameFiles
{
    /// <summary>
    ///     在 <paramref name="minecraftDirs" /> 的 <c>versions/*/</c> 中查找与 <paramref name="pins" /> 一致的客户端 jar。
    ///     先看名字含版本号的文件夹，再看其余文件夹；只对大小合理的 jar 计算 SHA-1。
    /// </summary>
    public static HorizonLocalClient? FindClient(IEnumerable<string> minecraftDirs, HorizonPins pins)
    {
        foreach (var minecraftDir in minecraftDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var versionsDir = Path.Combine(minecraftDir, "versions");
            if (!Directory.Exists(versionsDir)) continue;
            var folders = Directory.GetDirectories(versionsDir)
                .OrderByDescending(folder => Path.GetFileName(folder) == pins.Version)
                .ThenByDescending(folder => Path.GetFileName(folder).Contains(pins.Version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                var jar = Path.Combine(folder, name + ".jar");
                if (!File.Exists(jar) || new FileInfo(jar).Length < 1024 * 1024) continue;
                if (!string.Equals(Sha1OfFile(jar), pins.ClientSha1, StringComparison.OrdinalIgnoreCase)) continue;
                var json = Path.Combine(folder, name + ".json");
                return new HorizonLocalClient(
                    minecraftDir,
                    jar,
                    pins.IsAcceptedManifest(json) ? json : null,
                    Path.Combine(minecraftDir, "libraries"),
                    Path.Combine(minecraftDir, "assets"));
            }
        }

        return null;
    }

    /// <summary>
    ///     固定版本的官方版本 JSON 的下载地址（BMCLAPI 镜像在前，官方在后）。
    /// </summary>
    public static IReadOnlyList<string> ManifestUrls(HorizonPins pins) =>
    [
        $"https://bmclapi2.bangbang93.com/v1/packages/{pins.ManifestSha1s[0]}/{pins.Version}.json",
        $"https://piston-meta.mojang.com/v1/packages/{pins.ManifestSha1s[0]}/{pins.Version}.json"
    ];

    /// <summary>
    ///     服务端 jar 的下载地址（BMCLAPI 镜像在前，官方在后）。官方地址取自版本 JSON 的 downloads.server.url。
    /// </summary>
    public static IReadOnlyList<string> ServerJarUrls(HorizonPins pins, string versionJsonPath)
    {
        var urls = new List<string> { $"https://bmclapi2.bangbang93.com/version/{pins.Version}/server" };
        var official = JsonNode.Parse(File.ReadAllText(versionJsonPath))?["downloads"]?["server"]?["url"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(official)) urls.Add(official);
        return urls;
    }

    public static string Sha1OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }
}
