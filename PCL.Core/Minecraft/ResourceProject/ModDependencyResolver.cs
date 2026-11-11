using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PCL.Core.Minecraft.ResourceProject;

public sealed record class ModDependencyReference
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsRequired { get; init; } = true;
}

public sealed record class ModDependencyRequest
{
    public string TargetMinecraftVersion { get; init; } = string.Empty;
    public List<string> TargetLoaders { get; init; } = [];
    public List<ModDependencyReference> RequiredDependencies { get; init; } = [];
    public List<InstalledModIdentity> InstalledMods { get; init; } = [];
    public Func<string, string, ModDependencyProject?> ProjectResolver { get; init; } = (_, _) => null;
}

public sealed record class ModDependencyProject
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? ProjectName { get; init; }

    /// <summary>工程短名（例如 <c>forge-config-api-port</c>），用于与本地 Mod 的自述标识比对。</summary>
    public string? Slug { get; init; }

    public List<ModDependencyFile> Files { get; init; } = [];
}

public sealed record class ModDependencyFile
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Version { get; init; }
    public List<string> GameVersions { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
    public int ReleaseType { get; init; }
    public DateTime ReleaseDate { get; init; }
    public List<ModDependencyReference> RequiredDependencies { get; init; } = [];
}

public sealed record class InstalledModIdentity
{
    /// <summary>由 PCL 联网缓存识别出的工程 ID；本地文件未经联网识别时为 null。</summary>
    public string? SourceProjectId { get; init; }
    public string? Source { get; init; }

    /// <summary>Mod 自述的 ID（fabric.mod.json 的 id、mods.toml 的 modId 等）。</summary>
    public string? ModId { get; init; }

    /// <summary>本地文件名，用于与将要下载的文件名比对。</summary>
    public string? FileName { get; init; }

    /// <summary>Mod 自述的版本号。</summary>
    public string? Version { get; init; }

    /// <summary>可用来与工程短名比对的候选标识（Mod ID、主页 URL 中的 slug 等）。</summary>
    public List<string> SlugCandidates { get; init; } = [];

    /// <summary>已知的兼容游戏版本；本地 jar 通常为空，此时退化为约束判定。</summary>
    public List<string> GameVersions { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
}

public sealed record class ModDependencyResolutionResult
{
    public List<ResolvedDependencyInstall> ToInstall { get; } = [];
    public List<UnresolvedDependency> Unresolved { get; } = [];
    public List<IgnoredDependency> Satisfied { get; } = [];
}

public sealed record class ResolvedDependencyInstall
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public ModDependencyFile File { get; init; } = new();
}

public sealed record class UnresolvedDependency
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record class IgnoredDependency
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ModDependencyResolver
{
    private const int MaxDepth = 32;
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public ModDependencyResolutionResult Resolve(ModDependencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProjectResolver);

        var context = new ResolutionContext(request);
        foreach (var dependency in request.RequiredDependencies)
        {
            ResolveDependency(context, dependency, 0);
        }

        return context.Result;
    }

    private static void ResolveDependency(ResolutionContext context, ModDependencyReference dependency, int depth)
    {
        if (string.IsNullOrWhiteSpace(dependency.ProjectId) || string.IsNullOrWhiteSpace(dependency.Source))
        {
            return;
        }

        if (!dependency.IsRequired)
        {
            context.AddSatisfied(dependency.ProjectId, dependency.Source, "Optional dependency ignored.");
            return;
        }

        if (depth > MaxDepth)
        {
            context.AddUnresolved(dependency.ProjectId, dependency.Source, "Maximum dependency depth exceeded.");
            return;
        }

        var visitedKey = context.GetVisitedKey(dependency.ProjectId, dependency.Source);
        if (!context.Visited.Add(visitedKey))
        {
            return;
        }

        if (context.IsInstalledCompatible(dependency.ProjectId, dependency.Source))
        {
            context.AddSatisfied(dependency.ProjectId, dependency.Source, "Already installed and compatible.");
            return;
        }

        var project = context.Request.ProjectResolver(dependency.Source, dependency.ProjectId);
        if (project is null)
        {
            context.AddUnresolved(dependency.ProjectId, dependency.Source, "Dependency project was not found.");
            return;
        }

        // 本地 Mod 常没有联网标识（SourceProjectId 为空），此时用工程短名 / Mod ID 兜底认定“已装”。
        if (context.IsInstalledByAlias(project))
        {
            context.AddSatisfied(project.ProjectId, project.Source, "Already installed (matched by local mod identity).");
            return;
        }

        var selectedFile = SelectBestFile(project.Files, context.TargetMinecraftVersion, context.TargetLoaders);
        if (selectedFile is null)
        {
            context.AddUnresolved(project.ProjectId, project.Source, "No compatible file was found.");
            return;
        }

        context.AddInstall(project, selectedFile);

        foreach (var nestedDependency in selectedFile.RequiredDependencies)
        {
            ResolveDependency(context, nestedDependency, depth + 1);
        }
    }

    private static ModDependencyFile? SelectBestFile(
        IEnumerable<ModDependencyFile> files,
        string targetMinecraftVersion,
        HashSet<string> targetLoaders)
    {
        return files
            .Where(file => IsCompatibleFile(file, targetMinecraftVersion, targetLoaders))
            .OrderByDescending(file => HasExactGameVersionMatch(file, targetMinecraftVersion))
            .ThenByDescending(file => HasLoaderMatch(file, targetLoaders))
            .ThenBy(file => NormalizeReleaseType(file.ReleaseType))
            .ThenByDescending(file => file.ReleaseDate)
            .FirstOrDefault();
    }

    private static bool IsCompatibleFile(ModDependencyFile file, string targetMinecraftVersion, HashSet<string> targetLoaders)
    {
        if (!HasExactGameVersionMatch(file, targetMinecraftVersion))
        {
            return false;
        }

        if (targetLoaders.Count == 0)
        {
            return true;
        }

        if (file.Loaders.Count == 0)
        {
            return true;
        }

        return file.Loaders.Any(loader => targetLoaders.Contains(loader));
    }

    private static bool HasExactGameVersionMatch(ModDependencyFile file, string targetMinecraftVersion)
    {
        return file.GameVersions.Any(version => Comparer.Equals(version, targetMinecraftVersion));
    }

    private static bool HasLoaderMatch(ModDependencyFile file, HashSet<string> targetLoaders)
    {
        if (targetLoaders.Count == 0)
        {
            return true;
        }

        return file.Loaders.Any(loader => targetLoaders.Contains(loader));
    }

    /// <summary>把 slug / Mod ID 归一为只含小写字母数字的形式，抹平 <c>-</c>、<c>_</c> 与大小写差异。</summary>
    public static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static int NormalizeReleaseType(int releaseType)
    {
        return releaseType switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            _ => int.MaxValue,
        };
    }

    private sealed class ResolutionContext
    {
        private readonly HashSet<string> _installDedupe = new(Comparer);
        private readonly HashSet<string> _unresolvedDedupe = new(Comparer);
        private readonly HashSet<string> _satisfiedDedupe = new(Comparer);

        public ResolutionContext(ModDependencyRequest request)
        {
            Request = request;
            Result = new ModDependencyResolutionResult();
            Visited = new HashSet<string>(Comparer);
            TargetMinecraftVersion = request.TargetMinecraftVersion ?? string.Empty;
            TargetLoaders = new HashSet<string>(
                request.TargetLoaders.Where(static loader => !string.IsNullOrWhiteSpace(loader)),
                Comparer);
            LoaderSetKey = string.Join(",", TargetLoaders.OrderBy(static loader => loader, Comparer));
        }

        public ModDependencyRequest Request { get; }
        public ModDependencyResolutionResult Result { get; }
        public HashSet<string> Visited { get; }
        public string TargetMinecraftVersion { get; }
        public HashSet<string> TargetLoaders { get; }
        private string LoaderSetKey { get; }

        public string GetVisitedKey(string projectId, string source)
        {
            return $"{source}:{projectId}:{TargetMinecraftVersion}:{LoaderSetKey}";
        }

        public bool IsInstalledCompatible(string projectId, string source)
        {
            return Request.InstalledMods.Any(installed =>
                Comparer.Equals(installed.SourceProjectId, projectId)
                && Comparer.Equals(installed.Source, source)
                && installed.GameVersions.Any(version => Comparer.Equals(version, TargetMinecraftVersion))
                && LoadersCompatible(installed.Loaders));
        }

        /// <summary>
        ///     本地 Mod 往往没有 PCL 的联网标识，<see cref="IsInstalledCompatible" /> 会一律判为未安装。
        ///     这里改用工程短名与 Mod 自述标识比对，认定同一个前置是否已经在目标目录中。
        /// </summary>
        public bool IsInstalledByAlias(ModDependencyProject project)
        {
            var projectKeys = new List<string>();
            AddKey(projectKeys, project.Slug);
            AddKey(projectKeys, project.ProjectId);
            AddTrimmedKeys(projectKeys, project.Slug);
            if (projectKeys.Count == 0)
                return false;

            foreach (var installed in Request.InstalledMods)
            {
                if (!LoadersCompatible(installed.Loaders))
                    continue;

                // 文件已声明版本却不含目标版本时，交给后续流程重新下载兼容版本。
                if (installed.GameVersions.Count > 0
                    && !installed.GameVersions.Any(version => Comparer.Equals(version, TargetMinecraftVersion)))
                    continue;

                var installedKeys = new List<string>();
                AddKey(installedKeys, installed.SourceProjectId);
                AddKey(installedKeys, installed.ModId);
                foreach (var candidate in installed.SlugCandidates)
                {
                    AddKey(installedKeys, candidate);
                    AddTrimmedKeys(installedKeys, candidate);
                }

                AddTrimmedKeys(installedKeys, installed.ModId);

                if (installedKeys.Any(projectKeys.Contains))
                    return true;
            }

            return false;
        }

        private static void AddKey(List<string> keys, string? value)
        {
            var normalized = NormalizeIdentity(value);
            if (normalized.Length >= 3 && !keys.Contains(normalized))
                keys.Add(normalized);
        }

        /// <summary>
        ///     补一组「去掉拖尾修饰词」的候选。
        ///     工程短名和 Mod ID 之间经常只差一个泛用后缀：
        ///     Architectury 的 slug 是 <c>architectury-api</c>，jar 自述的 Mod ID 却是 <c>architectury</c>；
        ///     类似的还有 <c>xxx-lib</c> / <c>xxx-mod</c>。
        ///     只剥离明确的泛用后缀，避免把 <c>architectury</c> 和 <c>architectury-extras</c> 混为一谈。
        /// </summary>
        private static void AddTrimmedKeys(List<string> keys, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var segments = value.Trim('-', '_', '.', ' ')
                .Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                // 无分隔符时按纯文本处理，例如 architecturyapi → architectury。
                var normalized = NormalizeIdentity(value);
                AddTrimmedSuffix(keys, normalized);
                return;
            }

            var joined = string.Join("", segments);
            AddTrimmedSuffix(keys, joined);
        }

        private static readonly string[] GenericSuffixes = ["api", "lib", "library", "mod", "core"];

        private static void AddTrimmedSuffix(List<string> keys, string normalized)
        {
            if (normalized.Length < 3)
                return;

            foreach (var suffix in GenericSuffixes)
            {
                if (normalized.Length - suffix.Length < 3
                    || !normalized.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                var trimmed = normalized[..^suffix.Length];
                if (!keys.Contains(trimmed))
                    keys.Add(trimmed);
            }
        }

        public void AddInstall(ModDependencyProject project, ModDependencyFile file)
        {
            var dedupeKey = GetProjectKey(project.ProjectId, project.Source);
            if (!_installDedupe.Add(dedupeKey))
            {
                return;
            }

            Result.ToInstall.Add(new ResolvedDependencyInstall
            {
                ProjectId = project.ProjectId,
                Source = project.Source,
                ProjectName = project.ProjectName,
                File = file,
            });
        }

        public void AddUnresolved(string projectId, string source, string reason)
        {
            var dedupeKey = GetProjectKey(projectId, source);
            if (!_unresolvedDedupe.Add(dedupeKey))
            {
                return;
            }

            Result.Unresolved.Add(new UnresolvedDependency
            {
                ProjectId = projectId,
                Source = source,
                Reason = reason,
            });
        }

        public void AddSatisfied(string projectId, string source, string reason)
        {
            var dedupeKey = GetProjectKey(projectId, source);
            if (!_satisfiedDedupe.Add(dedupeKey))
            {
                return;
            }

            Result.Satisfied.Add(new IgnoredDependency
            {
                ProjectId = projectId,
                Source = source,
                Reason = reason,
            });
        }

        private bool LoadersCompatible(List<string> installedLoaders)
        {
            if (TargetLoaders.Count == 0 || installedLoaders.Count == 0)
            {
                return true;
            }

            return installedLoaders.Any(loader => TargetLoaders.Contains(loader));
        }

        private static string GetProjectKey(string projectId, string source)
        {
            return $"{source}:{projectId}";
        }
    }
}
