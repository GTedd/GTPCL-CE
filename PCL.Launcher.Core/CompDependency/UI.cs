using PCL.Core.App.Localization;
using PCL.Core.Minecraft.ResourceProject;
using CompFileStatus = PCL.Comp.CompFileStatus;
using CompLoaderType = PCL.Comp.CompLoaderType;

namespace PCL;

public static partial class CompDependency
{
    public static Comp.CompDepsInstallTypes ConfirmDependencyInstall(
        ModDependencyResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Unresolved is { Count: > 0 })
        {
            LauncherCommon.Log($"[CompDeps] 无法解析: {result.Unresolved.Count} 个必需前置");
            var dependencies = string.Join(
                Environment.NewLine,
                result.Unresolved.Select(dep => Lang.Text(
                    "Download.Comp.Dependency.Unresolved.ListItem",
                    dep.Source,
                    dep.ProjectId,
                    dep.Reason)));
            var message = Lang.Text("Download.Comp.Dependency.Unresolved.Message", dependencies);
            var selectedButton = CompUi.Current?.ShowDialog(
                    message,
                    Lang.Text("Download.Comp.Dependency.Unresolved.Title"),
                    Lang.Text("Download.Comp.Dependency.Unresolved.ContinueWithoutDependencies"),
                    Lang.Text("Common.Action.Cancel"),
                    isWarn: true)
                ?? 0;

            return selectedButton == 1
                ? Comp.CompDepsInstallTypes.Unresolved
                : Comp.CompDepsInstallTypes.Cancel;
        }

        if (result.ToInstall is { Count: > 0 })
        {
            var dependencies = string.Join(
                Environment.NewLine,
                result.ToInstall.Select(install => Lang.Text(
                    "Download.Comp.Dependency.Install.ListItem",
                    install.ProjectName,
                    install.Source,
                    FormatFileLabel(install.File))));
            var message = Lang.Text("Download.Comp.Dependency.Install.Message", dependencies);
            var dialogResult = CompUi.Current?.ShowDialog(
                    message,
                    Lang.Text("Download.Comp.Dependency.Install.Title"),
                    Lang.Text("Download.Comp.Dependency.Install.WithDependencies"),
                    Lang.Text("Download.Comp.Dependency.Install.WithoutDependencies"),
                    Lang.Text("Download.Comp.Dependency.Install.Cancel"))
                ?? 0;

            return dialogResult switch
            {
                1 => Comp.CompDepsInstallTypes.WithDeps,
                2 => Comp.CompDepsInstallTypes.WithoutDeps,
                3 => Comp.CompDepsInstallTypes.Cancel,
                _ => Comp.CompDepsInstallTypes.Cancel
            };
        }

        return Comp.CompDepsInstallTypes.WithDeps;
    }

    /// <summary>
    ///     组合前置文件的展示文本。
    ///     Modrinth 的版本名（<c>name</c>）常常自带版本号（如 <c>[Fabric 26.3] v22.0.5</c>），
    ///     而 <c>version_number</c> 是 <c>22.0.5+fabric</c>，直接拼接会得到
    ///     <c>v22.0.5 v22.0.5+fabric</c> 这种重复。这里只在版本号确实缺失时补一段。
    /// </summary>
    private static string FormatFileLabel(ModDependencyFile file)
    {
        var displayName = (file.DisplayName ?? string.Empty).Trim();
        var version = (file.Version ?? string.Empty).Trim();

        if (version.Length == 0)
            return displayName;
        if (displayName.Length == 0)
            return $"v{version}";

        // 去掉加载器后缀后再比对：版本名里通常只写 22.0.5，不带 +fabric。
        var plus = version.IndexOf('+');
        var versionCore = plus > 0 ? version[..plus] : version;
        if (versionCore.Length == 0
            || displayName.Contains(versionCore, StringComparison.OrdinalIgnoreCase))
            return displayName;

        return $"{displayName} v{version}";
    }

    private static string GetSource(bool fromCurseForge)
    {
        return fromCurseForge ? "CurseForge" : "Modrinth";
    }

    private static List<string> ToLoaderNames(IEnumerable<CompLoaderType>? loaders)
    {
        if (loaders is null) return [];

        return
        [
            .. loaders
                .Where(static loader => loader != CompLoaderType.Any)
                .Select(static loader => loader.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static int MapReleaseType(CompFileStatus status)
    {
        return status switch
        {
            CompFileStatus.Release => 1,
            CompFileStatus.Beta => 2,
            CompFileStatus.Alpha => 3,
            _ => 1
        };
    }
}