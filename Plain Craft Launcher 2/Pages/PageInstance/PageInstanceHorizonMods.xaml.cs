using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Horizon;
using PCL.Core.UI;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PCL;

/// <summary>
///     Horizon 实例的模组页：内容模组（<c>horizon-mods/</c>）与原生模组（<c>horizon-native-mods/</c>）的列表、开关、
///     安装与删除，以及原生模组的编译。规则见 Horizon 的 docs/api/content.md 与 docs/native-mods.md。
/// </summary>
public partial class PageInstanceHorizonMods : IRefreshable
{
    private static readonly Dictionary<string, HorizonContentSupport> _SupportCache = new(StringComparer.OrdinalIgnoreCase);

    private int _reloadVersion;
    private HorizonContentSupport _support = HorizonContentSupport.Unknown;
    private List<HorizonInstalledMod> _mods = [];

    public PageInstanceHorizonMods()
    {
        InitializeComponent();
        // 拖放由主窗口按扩展名统一处理（.hmod / .hnmod → InstallFiles），这里不再重复处理
        Loaded += (_, _) => Reload();
    }

    private static McInstance? Instance => PageInstanceLeft.McInstance;

    private static string? GameDir => Instance?.PathIndie.TrimEnd('\\');

    public void Refresh() => Reload();

    /// <summary>重新读取模组与构建信息。</summary>
    public void Reload()
    {
        var instance = Instance;
        if (instance is null || !HorizonInstance.IsHorizon(instance)) return;
        var version = Interlocked.Increment(ref _reloadVersion);
        var gameDir = GameDir!;

        Common.RunInNewThread(() =>
        {
            List<HorizonInstalledMod> mods;
            HorizonContentSupport support;
            try
            {
                mods = HorizonGameContent.List(gameDir);
                support = QuerySupport(instance);
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Horizon] 读取模组列表失败", Common.LogLevel.Hint);
                return;
            }

            Common.RunInUi(() =>
            {
                if (version != _reloadVersion) return;
                _mods = mods;
                _support = support;
                _Render();
                ReloadNativeBuild();
            });
        }, "Horizon Mods Reload");
    }

    /// <summary>
    ///     这个构建能加载什么（<c>horizon-client --launch-api</c> 的 content），按构建可执行文件缓存。
    /// </summary>
    public static HorizonContentSupport QuerySupport(McInstance instance)
    {
        var buildDir = HorizonNativeBuild.PlayedBuildDir(instance);
        var exe = Path.Combine(buildDir, HorizonProcess.ClientExe);
        if (!File.Exists(exe)) return HorizonContentSupport.Unknown;
        var key = exe + "|" + File.GetLastWriteTimeUtc(exe).Ticks;
        lock (_SupportCache)
        {
            if (_SupportCache.TryGetValue(key, out var cached)) return cached;
        }

        HorizonContentSupport support;
        try
        {
            support = HorizonProcess.QueryContentSupportAsync(buildDir).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Horizon] 读取构建支持的内容失败");
            support = HorizonContentSupport.Unknown;
        }

        lock (_SupportCache)
        {
            _SupportCache[key] = support;
        }

        return support;
    }

    private void _Render()
    {
        LabSupport.Text = _support.IsKnown
            ? Lang.Text("Instance.Horizon.Mods.Support",
                _support.ModApis.Count == 0 ? "-" : string.Join(", ", _support.ModApis),
                _support.NativeHooks?.ToString() ?? "-")
            : Lang.Text("Instance.Horizon.Mods.SupportUnknown");

        var content = _mods.Where(m => !m.InNativeFolder).ToList();
        var native = _mods.Where(m => m.InNativeFolder).ToList();

        PanContentMods.Children.Clear();
        foreach (var mod in content) PanContentMods.Children.Add(_CreateRow(mod));
        LabContentEmpty.Visibility = content.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardContent.Title = Lang.Text("Instance.Horizon.Mods.ContentCount", content.Count);

        PanNativeMods.Children.Clear();
        foreach (var mod in native) PanNativeMods.Children.Add(_CreateRow(mod));
        LabNativeEmpty.Visibility = native.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardNative.Title = Lang.Text("Instance.Horizon.Mods.NativeCount", native.Count);
    }

    private MyListItem _CreateRow(HorizonInstalledMod mod)
    {
        var info = mod.Info;
        var item = new MyListItem
        {
            Title = info?.Name ?? mod.Id,
            Info = _DescribeRow(mod),
            Type = MyListItem.CheckType.CheckBox,
            Checked = mod.Enabled,
            Height = 42d,
            SnapsToDevicePixels = true,
            Logo = $"{Common.PathImage}Blocks/{(mod.InNativeFolder ? "CommandBlock" : "Grass")}.png",
            Tag = mod,
            ToolTip = _ToolTip(mod)
        };
        var tags = new List<string>();
        if (!string.IsNullOrEmpty(info?.Version)) tags.Add(info.Version);
        if (info?.Kind == HorizonModKind.ContentAndNative) tags.Add(Lang.Text("Instance.Horizon.Mods.Tag.Combined"));
        if (mod.Error is not null) tags.Add(Lang.Text("Instance.Horizon.Mods.Tag.Error"));
        item.PlainTags = tags;

        item.Changed += (_, e) =>
        {
            if (!e.raiseByMouse) return;
            _Toggle(mod, item.Checked);
        };

        var btnFolder = new MyIconButton { SvgIcon = "lucide/folder-open", ToolTip = Lang.Text("Instance.Horizon.Mods.ShowInFolder") };
        btnFolder.Click += (_, _) => Common.OpenExplorer(mod.Path);
        var btnDelete = new MyIconButton { SvgIcon = "lucide/trash-2", ToolTip = Lang.Text("Instance.Horizon.Mods.Remove") };
        btnDelete.Click += (_, _) => _Remove(mod);
        item.Buttons = [btnFolder, btnDelete];
        return item;
    }

    private string _DescribeRow(HorizonInstalledMod mod)
    {
        if (mod.Error is not null) return Lang.Text("Instance.Horizon.Mods.Unreadable", mod.Error);
        var info = mod.Info!;
        if (mod.InNativeFolder)
        {
            var native = info.Native!;
            if (!_support.SupportsNativeHooks(native.Hooks))
                return Lang.Text("Instance.Horizon.Mods.NeedsNewerHorizon", native.Hooks!, _support.NativeHooks!);
            return Lang.Text("Instance.Horizon.Mods.NativeInfo", native.Game, native.Horizon[..7],
                native.Hooks?.ToString() ?? "-");
        }

        if (!_support.SupportsModApi(info.Api))
            return Lang.Text("Instance.Horizon.Mods.ApiUnsupported", info.Api ?? "-", string.Join(", ", _support.ModApis));
        return string.IsNullOrWhiteSpace(info.Description) ? info.Id : info.Description;
    }

    private static string _ToolTip(HorizonInstalledMod mod)
    {
        var info = mod.Info;
        var lines = new List<string> { mod.Path };
        if (info is not null)
        {
            lines.Add($"id: {info.Id}");
            if (info.Authors.Count > 0) lines.Add(Lang.Text("Instance.Horizon.Mods.Authors", string.Join(", ", info.Authors)));
            if (info.Api is not null) lines.Add($"api: {info.Api} · side: {info.Side}");
            if (info.Capabilities.Count > 0) lines.Add("capabilities: " + string.Join(", ", info.Capabilities));
            if (info.Native is { } native)
                lines.Add($"horizon: {native.Horizon}\nmixins: {native.Mixins}");
        }

        if (mod.Error is not null) lines.Add(mod.Error);
        return string.Join("\n", lines);
    }

    private void _Toggle(HorizonInstalledMod mod, bool enabled)
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        if (_IsGameRunning())
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Mods.GameRunning")));
            Reload();
            return;
        }

        try
        {
            HorizonGameContent.SetEnabled(gameDir, mod.Id, enabled);
            Common.Log($"[Horizon] {(enabled ? "打开" : "关闭")}模组 {mod.Id}");
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Horizon] 切换模组失败", Common.LogLevel.Hint);
        }

        Reload();
    }

    private void _Remove(HorizonInstalledMod mod)
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        if ((DialogService.Instance.Show(new TextDialogRequest
            {
                Title = Lang.Text("Instance.Horizon.Mods.RemoveTitle"),
                Content = Lang.Text("Instance.Horizon.Mods.RemoveMessage", mod.Info?.Name ?? mod.Id, mod.Path),
                Buttons = DialogButtons.OkCancel,
                IsWarn = true,
                ForceWait = true
            }).Result?.Button ?? 0) != 1)
            return;

        try
        {
            HorizonGameContent.Remove(gameDir, mod);
            HintService.Show(Hints.Success(Lang.Text("Instance.Horizon.Mods.Removed", mod.Info?.Name ?? mod.Id)));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Horizon] 删除模组失败", Common.LogLevel.Hint);
        }

        Reload();
    }

    private void BtnInstall_Click(object sender, MouseButtonEventArgs e)
    {
        var files = SystemDialogs.SelectFiles(
            Lang.Text("Instance.Horizon.Mods.FileFilter"),
            Lang.Text("Instance.Horizon.Mods.InstallFile"));
        if (files.Length == 0) return;
        InstallFiles(files);
    }

    private void BtnBrowse_Click(object sender, MouseButtonEventArgs e) => OpenBrowser();

    private void BtnOpenContent_Click(object sender, MouseButtonEventArgs e) => _OpenFolder(HorizonGameContent.ContentModsDir);

    private void BtnOpenNative_Click(object sender, MouseButtonEventArgs e) => _OpenFolder(HorizonGameContent.NativeModsDir);

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        lock (_SupportCache) _SupportCache.Clear();
        Reload();
    }

    private static void _OpenFolder(string name)
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        var path = Path.Combine(gameDir, name);
        Directory.CreateDirectory(path);
        Common.OpenExplorer(path);
    }

    /// <summary>是否把这些文件当作 Horizon 模组处理（扩展名为 .hmod / .hnmod）。</summary>
    public static bool IsHorizonModFile(string path) =>
        path.EndsWith(".hmod", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".hnmod", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     把模组文件安装到当前选中的 Horizon 实例。也供主窗口的文件拖放调用。
    /// </summary>
    /// <returns>当前实例不是 Horizon 实例时返回 false，调用方应按其他方式处理。</returns>
    public static bool InstallFiles(IEnumerable<string> files)
    {
        var instance = Instance ?? InstanceList.McMcInstanceSelected;
        if (instance is null || !HorizonInstance.IsHorizon(instance))
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Mods.NoHorizonInstance")));
            return false;
        }

        var gameDir = instance.PathIndie.TrimEnd('\\');
        var list = files.ToList();
        Common.RunInNewThread(() =>
        {
            var installed = new List<string>();
            foreach (var file in list)
            {
                try
                {
                    HorizonModInfo info;
                    try
                    {
                        info = HorizonGameContent.Install(gameDir, file, false);
                    }
                    catch (IOException) when (File.Exists(file) || Directory.Exists(file))
                    {
                        if (!HorizonModFile.TryRead(file, out var incoming, out _) || incoming is null) throw;
                        if ((DialogService.Instance.Show(new TextDialogRequest
                            {
                                Title = Lang.Text("Instance.Horizon.Mods.ReplaceTitle"),
                                Content = Lang.Text("Instance.Horizon.Mods.ReplaceMessage", incoming.Name, incoming.Version),
                                Buttons = DialogButtons.OkCancel,
                                ForceWait = true
                            }).Result?.Button ?? 0) != 1)
                            continue;
                        info = HorizonGameContent.Install(gameDir, file, true);
                    }

                    installed.Add(info.Name);
                    Common.Log($"[Horizon] 已安装模组 {info.Id} {info.Version}（{info.Kind}）到 {info.Path}");
                    if (info.HasNativeCode) _ShowNativeTrustNoticeOnce();
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Mods.InstallFailed", Path.GetFileName(file), ex.Message)));
                }
            }

            if (installed.Count > 0)
                HintService.Show(Hints.Success(Lang.Text("Instance.Horizon.Mods.Installed", string.Join(", ", installed))));
            Common.RunInUi(() => Launcher.frmInstanceHorizonMods?.Reload());
        }, "Horizon Mods Install");
        return true;
    }

    /// <summary>首次安装原生模组时提示一次：原生模组不在沙盒里运行，需要编译后才生效。</summary>
    private static void _ShowNativeTrustNoticeOnce()
    {
        if (States.Hint.HorizonNativeTrust) return;
        States.Hint.HorizonNativeTrust = true;
        DialogService.Instance.Show(new TextDialogRequest
        {
            Title = Lang.Text("Instance.Horizon.Native.TrustTitle"),
            Content = Lang.Text("Instance.Horizon.Native.TrustNotice"),
            IsWarn = true,
            ForceWait = true
        });
    }

    /// <summary>这个实例的 Horizon 是否正在运行：游戏只在启动时读取模组与开关，运行中修改会被它覆盖。</summary>
    private static bool _IsGameRunning()
    {
        try
        {
            return GameLaunch.mcLaunchProcess is { HasExited: false } process &&
                   process.ProcessName.StartsWith("horizon", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
