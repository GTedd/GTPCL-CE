using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Horizon;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PCL;

/// <summary>模组页的“浏览 Horizon 模组”视图：搜索 Horizon 官方注册表并安装、更新（规则见 docs/api/mod-index.md）。</summary>
public partial class PageInstanceHorizonMods
{
    private const int MaxResults = 80;
    private HorizonRegistryIndex? _index;
    private bool _browseInitialized;
    private int _browseVersion;

    private static string IndexCacheFile =>
        Path.Combine(LauncherPaths.ExecutableDirectory, "PCL", "Horizon", "horizon-index.json");

    /// <summary>切换到浏览视图。</summary>
    public void OpenBrowser()
    {
        if (!_browseInitialized)
        {
            foreach (var (key, kind) in new[]
                     {
                         ("Instance.Horizon.Browser.Kind.Mod", HorizonBrowseKind.Mod),
                         ("Instance.Horizon.Browser.Kind.Shader", HorizonBrowseKind.Shader),
                         ("Instance.Horizon.Browser.Kind.ResourcePack", HorizonBrowseKind.ResourcePack)
                     })
                BrowseKind.Items.Add(new MyComboBoxItem { Content = Lang.Text(key), Tag = kind });
            foreach (var (key, sort) in new[]
                     {
                         ("Instance.Horizon.Browser.Sort.Downloads", HorizonRegistrySort.Downloads),
                         ("Instance.Horizon.Browser.Sort.Updated", HorizonRegistrySort.Updated),
                         ("Instance.Horizon.Browser.Sort.Newest", HorizonRegistrySort.Newest)
                     })
                BrowseSort.Items.Add(new MyComboBoxItem { Content = Lang.Text(key), Tag = sort });
            BrowseKind.SelectedIndex = 0;
            BrowseSort.SelectedIndex = 0;
            _browseInitialized = true;
        }

        PanMain.Visibility = Visibility.Collapsed;
        PanBrowse.Visibility = Visibility.Visible;
        _LoadIndex(false);
    }

    private void BrowseBack_Click(object sender, MouseButtonEventArgs e)
    {
        PanBrowse.Visibility = Visibility.Collapsed;
        PanMain.Visibility = Visibility.Visible;
        Reload();
    }

    private void BrowseSearch_Search(object sender, EventArgs e) => _RunSearch();

    private void BrowseFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_browseInitialized) _RunSearch();
    }

    private void _LoadIndex(bool force)
    {
        LabBrowseStatus.Text = Lang.Text("Instance.Horizon.Browser.Loading");
        Common.RunInNewThread(() =>
        {
            try
            {
                var index = HorizonRegistry.GetIndexAsync(HorizonInstance.Http, IndexCacheFile, force).GetAwaiter().GetResult();
                Common.RunInUi(() =>
                {
                    _index = index;
                    _RunSearch();
                });
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Horizon] 获取模组注册表失败");
                Common.RunInUi(() => LabBrowseStatus.Text = Lang.Text("Instance.Horizon.Browser.LoadFailed", ex.Message));
            }
        }, "Horizon Registry");
    }

    private void _RunSearch()
    {
        var index = _index;
        var instance = Instance;
        if (index is null || instance is null) return;
        var kind = (BrowseKind.SelectedItem as MyComboBoxItem)?.Tag as HorizonBrowseKind? ?? HorizonBrowseKind.Mod;
        var sort = (BrowseSort.SelectedItem as MyComboBoxItem)?.Tag as HorizonRegistrySort? ?? HorizonRegistrySort.Downloads;
        var query = BrowseSearch.Text;
        var version = Interlocked.Increment(ref _browseVersion);
        var gameDir = instance.PathIndie.TrimEnd('\\');

        Common.RunInNewThread(() =>
        {
            var results = HorizonRegistry.Search(index, kind, query, null, sort);
            var entries = HorizonRegistryInstaller.ReadEntries(gameDir).ToDictionary(e => e.Project);
            var support = QuerySupport(instance);
            var played = _PlayedShortCommit(instance);
            Common.RunInUi(() =>
            {
                if (version != _browseVersion) return;
                PanBrowseResults.Children.Clear();
                foreach (var project in results.Take(MaxResults))
                    PanBrowseResults.Children.Add(_CreateProjectRow(index, project, entries.GetValueOrDefault(project.Id), support, played));
                CardBrowseResults.Title = Lang.Text("Instance.Horizon.Browser.ResultCount", results.Count);
                LabBrowseStatus.Text = results.Count > MaxResults
                    ? Lang.Text("Instance.Horizon.Browser.TooMany", MaxResults)
                    : Lang.Text("Instance.Horizon.Browser.Source", index.Generated);
            });
        }, "Horizon Registry Search");
    }

    private static string? _PlayedShortCommit(McInstance instance)
    {
        var version = instance.JsonObject?[HorizonInstance.DataKey]?["version"]?.ToString();
        return version is null ? null : version[(version.LastIndexOf('-') + 1)..];
    }

    private MyListItem _CreateProjectRow(HorizonRegistryIndex index, HorizonRegistryProject project, HorizonBrowserEntry? installed,
        HorizonContentSupport support, string? played)
    {
        var pick = HorizonRegistry.PickVersion(index, project, support, played);
        var tags = new List<string>
        {
            project.Kind switch
            {
                HorizonRegistryKind.NativeMod => Lang.Text("Instance.Horizon.Browser.Tag.Native"),
                HorizonRegistryKind.Mod => Lang.Text("Instance.Horizon.Browser.Tag.Content"),
                _ => project.KindName
            },
            pick?.Version ?? project.Versions[0].Version,
            Lang.Text("Instance.Horizon.Browser.Tag.Downloads", project.Downloads)
        };
        if (installed is not null)
            tags.Add(pick is not null && pick.Version != installed.Version
                ? Lang.Text("Instance.Horizon.Browser.Tag.Update", installed.Version)
                : Lang.Text("Instance.Horizon.Browser.Tag.Installed"));
        if (project.IsOfficial) tags.Add(Lang.Text("Instance.Horizon.Browser.Tag.Official"));

        var item = new MyListItem
        {
            Title = project.Name,
            Info = project.Summary,
            Type = MyListItem.CheckType.Clickable,
            Height = 42d,
            SnapsToDevicePixels = true,
            Logo = project.Icon ?? $"{Common.PathImage}Blocks/{(project.Kind == HorizonRegistryKind.NativeMod ? "CommandBlock" : "Grass")}.png",
            PlainTags = tags,
            Tag = project
        };
        item.Click += (_, _) => _ShowProjectDetail(index, project, installed, support, played);

        var btnInstall = new MyIconButton
        {
            SvgIcon = "lucide/download",
            ToolTip = Lang.Text(installed is null ? "Instance.Horizon.Browser.Install" : "Instance.Horizon.Browser.UpdateOrReinstall")
        };
        btnInstall.Click += (_, _) => _InstallProject(index, project, support, played);
        var btnPage = new MyIconButton { SvgIcon = "lucide/globe", ToolTip = Lang.Text("Instance.Horizon.Browser.OpenPage") };
        btnPage.Click += (_, _) => Basics.OpenPath(project.Page);
        ToolTipService.SetPlacement(btnInstall, PlacementMode.Center);
        item.Buttons = [btnInstall, btnPage];
        return item;
    }

    private void _ShowProjectDetail(HorizonRegistryIndex index, HorizonRegistryProject project, HorizonBrowserEntry? installed,
        HorizonContentSupport support, string? played)
    {
        var pick = HorizonRegistry.PickVersion(index, project, support, played);
        var newest = project.Versions[0];
        var lines = new List<string>
        {
            project.Summary,
            "",
            Lang.Text("Instance.Horizon.Browser.Detail.Authors", string.Join(", ", project.Authors), project.License),
            Lang.Text("Instance.Horizon.Browser.Detail.Signed",
                Lang.Text(project.Key is null ? "Instance.Horizon.Browser.Unsigned" : "Instance.Horizon.Browser.Signed")),
            pick is not null
                ? Lang.Text("Instance.Horizon.Browser.Detail.Version", pick.Version, pick.Published)
                : Lang.Text("Instance.Horizon.Browser.Detail.Blocked",
                    HorizonRegistry.BlockedReason(index, project, newest, support) ?? Lang.Text("Instance.Horizon.Browser.NoVersion"))
        };
        if (installed is not null) lines.Add(Lang.Text("Instance.Horizon.Browser.Detail.Installed", installed.Version));
        if (project.Kind == HorizonRegistryKind.NativeMod)
            lines.Add(Lang.Text("Instance.Horizon.Browser.Detail.NativeNote"));
        if (!project.IsOfficial) lines.Add(Lang.Text("Instance.Horizon.Browser.Detail.NotEndorsed"));
        var description = project.Description.Length > 1200 ? project.Description[..1200] + "…" : project.Description;
        if (description.Length > 0) lines.AddRange(["", description]);

        if ((DialogService.Instance.Show(new TextDialogRequest
            {
                Title = project.Name,
                Content = string.Join("\n", lines),
                Buttons = pick is null
                    ? DialogButtons.Create("Instance.Horizon.Browser.OpenPage", "Common.Action.Cancel")
                    : DialogButtons.Create("Instance.Horizon.Browser.Install", "Instance.Horizon.Browser.OpenPage", "Common.Action.Cancel"),
                ForceWait = true
            }).Result?.Button ?? 0) is var button && button == 0)
            return;
        if (pick is null)
        {
            if (button == 1) Basics.OpenPath(project.Page);
            return;
        }

        if (button == 1) _InstallProject(index, project, support, played);
        else if (button == 2) Basics.OpenPath(project.Page);
    }

    private void _InstallProject(HorizonRegistryIndex index, HorizonRegistryProject project, HorizonContentSupport support, string? played)
    {
        var instance = Instance;
        if (instance is null) return;
        if (project.Kind is HorizonRegistryKind.DataPack or HorizonRegistryKind.Modpack or HorizonRegistryKind.Other)
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Browser.KindUnsupported", project.KindName)));
            return;
        }

        if (_IsGameRunning())
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Mods.GameRunning")));
            return;
        }

        var pick = HorizonRegistry.PickVersion(index, project, support, played);
        if (pick is null)
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Browser.Detail.Blocked",
                HorizonRegistry.BlockedReason(index, project, project.Versions[0], support) ?? Lang.Text("Instance.Horizon.Browser.NoVersion"))));
            return;
        }

        if (project.Kind == HorizonRegistryKind.NativeMod) _ShowNativeTrustNoticeOnce();
        var gameDir = instance.PathIndie.TrimEnd('\\');
        HintService.Show(Hints.Info(Lang.Text("Instance.Horizon.Browser.Installing", project.Name, pick.Version)));
        Common.RunInNewThread(() =>
        {
            try
            {
                var entry = HorizonRegistryInstaller.InstallAsync(HorizonInstance.Http, index, project, pick, gameDir,
                    HorizonRelease.BuiltInGithubProxies).GetAwaiter().GetResult();
                Common.Log($"[Horizon] 从注册表安装 {project.Id} {entry.Version} 到 {entry.File}（{entry.Verdict}）");
                HintService.Show(Hints.Success(Lang.Text(
                    project.Kind == HorizonRegistryKind.NativeMod ? "Instance.Horizon.Browser.InstalledNative" : "Instance.Horizon.Browser.Installed",
                    project.Name, entry.Version)));
            }
            catch (Exception ex)
            {
                Common.Log(ex, $"[Horizon] 安装 {project.Id} 失败");
                HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Browser.InstallFailed", project.Name, ex.Message)));
            }

            Common.RunInUi(_RunSearch);
        }, "Horizon Registry Install");
    }
}
