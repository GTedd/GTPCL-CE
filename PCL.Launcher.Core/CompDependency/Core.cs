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
        }

        return result;
    }

    /// <summary>收集可用来和工程短名比对的本地标识：Mod ID、内嵌提供的 ID、主页 URL 的最后一段。</summary>
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

        return candidates;

        static void AddCandidate(List<string> list, string? value)
        {
            var normalized = ModDependencyResolver.NormalizeIdentity(value);
            if (normalized.Length >= 3 && !list.Contains(normalized))
                list.Add(normalized);
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