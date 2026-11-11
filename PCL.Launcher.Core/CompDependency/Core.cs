using PCL.Core.Minecraft.ResourceProject;
using CompFile = PCL.Comp.CompFile;
using CompLoaderType = PCL.Comp.CompLoaderType;
using CompProject = PCL.Comp.CompProject;
using LocalCompFile = PCL.LocalComp.LocalCompFile;

namespace PCL;

public static partial class CompDependency
{
    public static ModDependencyRequest BuildRequest(
        CompFile file,
        CompProject project,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        string targetModsFolder)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(project);

        var source = GetSource(project.FromCurseForge);
        var dependencies = file
            .Dependencies
            .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
            .Select(dependencyId => new ModDependencyReference
            {
                ProjectId = dependencyId,
                Source = source,
                IsRequired = true
            })
            .Concat(
                file
                    .OptionalDependencies
                    .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                    .Select(dependencyId => new ModDependencyReference
                    {
                        ProjectId = dependencyId,
                        Source = source,
                        IsRequired = false
                    }))
            .ToList();

        return new ModDependencyRequest
        {
            TargetMinecraftVersion = targetMinecraftVersion,
            TargetLoaders = ToLoaderNames(targetLoaders),
            RequiredDependencies = dependencies,
            InstalledMods = ScanInstalledMods(targetModsFolder),
            ProjectResolver = ResolveProjectFiles
        };
    }

    public static List<InstalledModIdentity> ScanInstalledMods(string targetModsFolder)
    {
        var result = new List<InstalledModIdentity>();
        if (string.IsNullOrWhiteSpace(targetModsFolder) || !Directory.Exists(targetModsFolder))
            return result;

        // 与 result 一一对应，供后续按 SHA1 补齐联网身份。
        var hashPending = new List<LocalCompFile>();

        foreach (var path in Directory.GetFiles(targetModsFolder))
        {
            if (!LocalCompFile.IsModFile(path)) continue;

            var localFile = new LocalCompFile(path);
            localFile.Load();

            var source = localFile.Comp is null
                ? null
                : GetSource(localFile.Comp.FromCurseForge);
            var gameVersions = localFile
                    .CompFile?.GameVersions
                    ?.Where(static version => !string.IsNullOrWhiteSpace(version))
                    .Select(Comp.NormalizeGameVersion)
                    .Where(static version => !string.IsNullOrWhiteSpace(version))
                    .ToList()
                ?? [];
            var loaders = ToLoaderNames(localFile.CompFile?.ModLoaders);

            // 本地 jar 多半没有联网缓存，此时靠 Mod ID、内嵌提供者与主页 slug 认出“已经装过的是谁”。
            var identity = new InstalledModIdentity
            {
                SourceProjectId = localFile.Comp?.Id ?? localFile.CompFile?.ProjectId,
                Source = localFile.Comp is not null
                    ? source
                    : localFile.CompFile is not null
                        ? GetSource(localFile.CompFile.FromCurseForge)
                        : null,
                ModId = localFile.ModId,
                FileName = localFile.RawFileName,
                Version = localFile.Version,
                SlugCandidates = CollectSlugCandidates(localFile),
                GameVersions = gameVersions,
                Loaders = loaders
            };

            if (string.IsNullOrWhiteSpace(identity.SourceProjectId))
            {
                identity = identity with
                {
                    SourceProjectId = null,
                    Source = null
                };
            }

            result.Add(identity);
            hashPending.Add(localFile);
        }

        FillModrinthIdentities(result, hashPending);

        return result;
    }

    /// <summary>
    ///     用 SHA1 向 Modrinth 反查工程 ID。
    ///     下载页的本地 jar 不会经过实例资源列表的联网识别（<see cref="LocalCompFile.Comp" /> 为空），
    ///     因此“工程 ID + 工程短名”这套精确匹配全都失效，只剩 Mod ID 一条路；
    ///     而工程短名与 Mod ID 并不总是同一个字符串（例如 Architectury 的 slug 是
    ///     <c>architectury-api</c>，Mod ID 却是 <c>architectury</c>），于是已装的前置被当成没装。
    ///     这里按官方 <c>version_files</c> 接口补一次反查，一次请求覆盖整个 mods 目录。
    ///     任何失败都只是退回到旧的比对方式，不影响下载流程。
    /// </summary>
    private static void FillModrinthIdentities(
        List<InstalledModIdentity> identities,
        List<LocalCompFile> localFiles)
    {
        if (localFiles.Count == 0 || identities.Count == 0)
            return;

        try
        {
            var hashToIndexes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < localFiles.Count && i < identities.Count; i++)
            {
                // 已经有联网身份的项无需再查。
                if (!string.IsNullOrWhiteSpace(identities[i].SourceProjectId))
                    continue;

                var hash = localFiles[i].ModrinthHash;
                if (string.IsNullOrWhiteSpace(hash))
                    continue;

                if (!hashToIndexes.TryGetValue(hash, out var list))
                {
                    list = [];
                    hashToIndexes[hash] = list;
                }

                list.Add(i);
            }

            if (hashToIndexes.Count == 0)
                return;

            var body = $$"""{"hashes": ["{{string.Join("\",\"", hashToIndexes.Keys)}}"], "algorithm": "sha1"}""";
            var response = GameDownload.DlModRequest<JsonObject>(
                "https://api.modrinth.com/v2/version_files",
                "POST",
                body,
                "application/json");

            var matched = 0;
            foreach (var (hash, versionJson) in response)
            {
                if (!hashToIndexes.TryGetValue(hash, out var indexes))
                    continue;

                var projectId = versionJson?["project_id"]?.ToString();
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;

                var loaders = ToLoaderNames(
                    versionJson?["loaders"]?
                        .AsArray()
                        .Select(loader => ParseLoaderType(loader?.ToString()))
                        .OfType<CompLoaderType>());

                var apiGameVersions = versionJson?["game_versions"]?
                    .AsArray()
                    .Select(version => Comp.NormalizeGameVersion(version?.ToString()))
                    .Where(version => !string.IsNullOrWhiteSpace(version))
                    .ToList() ?? [];

                foreach (var index in indexes)
                {
                    var current = identities[index];
                    identities[index] = current with
                    {
                        SourceProjectId = projectId,
                        Source = "Modrinth",
                        // 版本接口给出的游戏版本比连 fabric.mod.json 更可靠，能覆盖依赖范围。
                        GameVersions = current.GameVersions.Count > 0 ? current.GameVersions : apiGameVersions,
                        Loaders = current.Loaders.Count > 0 ? current.Loaders : loaders
                    };
                    matched++;
                }
            }

            LauncherCommon.Log($"[CompDeps] 通过 Modrinth 反向识别出 {matched}/{hashToIndexes.Count} 个本地 Mod 的工程身份");
        }
        catch (Exception ex)
        {
            // 前置检测是下载前的附加步骤，联网失败不应该打断下载。
            LauncherCommon.Log(ex, "[CompDeps] 通过 Modrinth 识别本地 Mod 身份失败，退回本地标识比对", LauncherLogLevel.Developer);
        }
    }

    private static CompLoaderType? ParseLoaderType(string? loader)
    {
        return loader?.ToLowerInvariant() switch
        {
            "fabric" => Comp.CompLoaderType.Fabric,
            "quilt" => Comp.CompLoaderType.Quilt,
            "forge" => Comp.CompLoaderType.Forge,
            "neoforge" => Comp.CompLoaderType.NeoForge,
            _ => null
        };
    }

    /// <summary>
    ///     收集可用来和工程短名比对的本地标识：Mod ID、内嵌提供的 ID、主页 URL 的最后一段，
    ///     以及 jar 文件名去掉版本号后的主体（<c>architectury-fabric-22.0.5.jar</c> → <c>architectury</c>）。
    /// </summary>
    private static List<string> CollectSlugCandidates(LocalCompFile localFile)
    {
        var candidates = new List<string>();

        AddCandidate(candidates, localFile.ModId);
        foreach (var providedId in localFile.ProvidedIds)
            AddCandidate(candidates, providedId);

        // 主页 URL 只在 Modrinth / CurseForge 上取最后一段作为 slug，
        // 其它站点（如 GitHub 仓库名）与工程短名并不等价，拿去比对会误判。
        var url = localFile.Url;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var trimmed = url.TrimEnd('/');
            if (trimmed.Contains("modrinth.com", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("curseforge.com", StringComparison.OrdinalIgnoreCase))
            {
                var lastSlash = trimmed.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash + 1 < trimmed.Length)
                    AddCandidate(candidates, trimmed[(lastSlash + 1)..]);
            }
        }

        AddFileNameCandidate(candidates, localFile.RawFileName);

        return candidates;

        static void AddCandidate(List<string> list, string? value)
        {
            var normalized = ModDependencyResolver.NormalizeIdentity(value);
            if (normalized.Length >= 3 && !list.Contains(normalized))
                list.Add(normalized);
        }

        // 文件名兜底：去掉扩展名与 -fabric / -1.20.1 / -22.0.5 这类尾巴。
        static void AddFileNameCandidate(List<string> list, string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(stem))
                return;

            var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                return;

            // 取第一个「看起来像版本号」之前的部分；没有版本号时整段保留。
            var head = new List<string>();
            foreach (var part in parts)
            {
                if (LooksLikeVersion(part) || IsLoaderToken(part))
                    break;
                head.Add(part);
            }

            if (head.Count == 0)
                return;

            AddCandidate(list, string.Join('-', head));
            // 单个词也收进去，例如 architectury-fabric-22.0.5 的主体就是 architectury。
            if (head.Count > 1)
                AddCandidate(list, head[0]);

            static bool IsLoaderToken(string value) =>
                value.Equals("fabric", StringComparison.OrdinalIgnoreCase)
                || value.Equals("forge", StringComparison.OrdinalIgnoreCase)
                || value.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
                || value.Equals("quilt", StringComparison.OrdinalIgnoreCase);

            static bool LooksLikeVersion(string value) =>
                value.Length > 0
                && char.IsDigit(value[0])
                && value.Contains('.');
        }
    }

    public static ModDependencyProject? ResolveProjectFiles(string source, string projectId)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
            return null;

        var fromCurseForge =
            string.Equals(source, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var files = Comp.CompFilesGet(projectId, fromCurseForge);
        if (!Comp.compProjectCache.TryGetValue(projectId, out var compProject))
            return null;

        if (compProject.FromCurseForge != fromCurseForge)
            return null;

        return new ModDependencyProject
        {
            ProjectId = compProject.Id,
            Source = source,
            ProjectName = compProject.TranslatedName ?? compProject.RawName,
            Slug = compProject.Slug,
            Files =
            [
                .. files.Select(compFile => new ModDependencyFile
                {
                    Id = compFile.Id,
                    DisplayName = compFile.DisplayName,
                    Version = compFile.Version,
                    GameVersions = compFile
                            .GameVersions
                            ?.Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .Select(Comp.NormalizeGameVersion)
                            .Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .ToList()
                        ?? [],
                    Loaders = ToLoaderNames(compFile.ModLoaders),
                    ReleaseType = MapReleaseType(compFile.Status),
                    ReleaseDate = compFile.ReleaseDate,
                    RequiredDependencies =
                    [
                        .. compFile
                            .Dependencies
                            .Where(static dependencyId => !string.IsNullOrWhiteSpace(dependencyId))
                            .Select(dependencyId => new ModDependencyReference
                            {
                                ProjectId = dependencyId,
                                Source = source,
                                IsRequired = true
                            })
                    ]
                })
            ]
        };
    }
}