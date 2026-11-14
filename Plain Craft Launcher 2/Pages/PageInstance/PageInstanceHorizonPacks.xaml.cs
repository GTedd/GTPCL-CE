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
///     Horizon 实例的光影包、资源包与数据包页，开关规则与官方启动器相同（见 <see cref="HorizonPacks" />）。
/// </summary>
public partial class PageInstanceHorizonPacks : IRefreshable
{
    private int _reloadVersion;

    public PageInstanceHorizonPacks()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            e.Handled = true;
            _AddFiles(files);
        };
    }

    private static McInstance? Instance => PageInstanceLeft.McInstance;
    private static string? GameDir => Instance?.PathIndie.TrimEnd('\\');

    public void Refresh() => Reload();

    public void Reload()
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        var version = Interlocked.Increment(ref _reloadVersion);
        var world = (ComboWorld.SelectedItem as MyComboBoxItem)?.Tag as string;
        Common.RunInNewThread(() =>
        {
            List<HorizonPack> shaders, resources;
            List<string> worlds;
            try
            {
                shaders = HorizonPacks.ListShaders(gameDir);
                resources = HorizonPacks.ListResourcePacks(gameDir);
                var saves = Path.Combine(gameDir, "saves");
                worlds = Directory.Exists(saves)
                    ? Directory.EnumerateDirectories(saves).Where(d => File.Exists(Path.Combine(d, "level.dat"))).OrderBy(d => d).ToList()
                    : [];
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Horizon] 读取包列表失败", Common.LogLevel.Hint);
                return;
            }

            Common.RunInUi(() =>
            {
                if (version != _reloadVersion) return;
                _RenderList(PanShaders, LabShadersEmpty, CardShaders, "Instance.Horizon.Packs.ShadersCount", shaders);
                _RenderList(PanResources, LabResourcesEmpty, CardResources, "Instance.Horizon.Packs.ResourcesCount", resources);

                ComboWorld.SelectionChanged -= ComboWorld_Changed;
                ComboWorld.Items.Clear();
                foreach (var dir in worlds) ComboWorld.Items.Add(new MyComboBoxItem { Content = Path.GetFileName(dir), Tag = dir });
                var selected = worlds.IndexOf(world ?? "");
                ComboWorld.SelectedIndex = selected >= 0 ? selected : worlds.Count > 0 ? 0 : -1;
                ComboWorld.SelectionChanged += ComboWorld_Changed;
                _ReloadDataPacks();
            });
        }, "Horizon Packs Reload");
    }

    private void _RenderList(StackPanel panel, TextBlock empty, MyCard card, string titleKey, List<HorizonPack> packs)
    {
        panel.Children.Clear();
        foreach (var pack in packs) panel.Children.Add(_CreateRow(pack, null));
        empty.Visibility = packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        card.Title = Lang.Text(titleKey, packs.Count);
    }

    private MyListItem _CreateRow(HorizonPack pack, string? worldDir)
    {
        var caption = string.IsNullOrWhiteSpace(pack.Description)
            ? pack.File
            : pack.Title == pack.File ? pack.Description : $"{pack.Description} · {pack.File}";
        if (pack.Problem is not null) caption = $"{pack.Problem} · {caption}";
        var item = new MyListItem
        {
            Title = pack.Title,
            Info = caption,
            Type = MyListItem.CheckType.CheckBox,
            Checked = pack.Enabled,
            Height = 42d,
            SnapsToDevicePixels = true,
            Logo = $"{Common.PathImage}Blocks/{pack.Kind switch
            {
                HorizonPackKind.ResourcePack => "GrassPath",
                HorizonPackKind.DataPack => "CommandBlock",
                _ => "RedstoneLampOn"
            }}.png",
            ToolTip = pack.Path
        };
        item.Changed += (_, e) =>
        {
            if (!e.raiseByMouse) return;
            _Toggle(pack, item.Checked, worldDir);
        };
        var btnFolder = new MyIconButton { SvgIcon = "lucide/folder-open", ToolTip = Lang.Text("Instance.Horizon.Mods.ShowInFolder") };
        btnFolder.Click += (_, _) => Common.OpenExplorer(pack.Path);
        var btnDelete = new MyIconButton { SvgIcon = "lucide/trash-2", ToolTip = Lang.Text("Instance.Horizon.Mods.Remove") };
        btnDelete.Click += (_, _) => _Remove(pack, worldDir);
        item.Buttons = [btnFolder, btnDelete];
        return item;
    }

    private void _Toggle(HorizonPack pack, bool enabled, string? worldDir)
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        if (_IsGameRunning())
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Packs.GameRunning")));
            Reload();
            return;
        }

        try
        {
            switch (pack.Kind)
            {
                case HorizonPackKind.ResourcePack:
                    HorizonPacks.SetResourcePackEnabled(gameDir, pack, enabled);
                    break;
                case HorizonPackKind.DataPack when worldDir is not null:
                    HorizonPacks.SetDataPackEnabled(worldDir, pack, enabled);
                    break;
                default:
                    HorizonPacks.SetShaderEnabled(gameDir, pack, enabled);
                    break;
            }

            HintService.Show(Hints.Info(Lang.Text(enabled ? "Instance.Horizon.Packs.On" : "Instance.Horizon.Packs.Off", pack.Title)));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Horizon] 切换包失败", Common.LogLevel.Hint);
        }

        Reload();
    }

    private void _Remove(HorizonPack pack, string? worldDir)
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        if (_IsGameRunning())
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Packs.GameRunning")));
            return;
        }

        if ((DialogService.Instance.Show(new TextDialogRequest
            {
                Title = Lang.Text("Instance.Horizon.Mods.RemoveTitle"),
                Content = Lang.Text("Instance.Horizon.Mods.RemoveMessage", pack.Title, pack.Path),
                Buttons = DialogButtons.OkCancel,
                IsWarn = true,
                ForceWait = true
            }).Result?.Button ?? 0) != 1)
            return;
        try
        {
            if (pack.Kind == HorizonPackKind.DataPack && worldDir is not null) HorizonPacks.RemoveDataPack(worldDir, pack);
            else HorizonPacks.RemovePack(gameDir, pack);
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Horizon] 删除包失败", Common.LogLevel.Hint);
        }

        Reload();
    }

    private void BtnAdd_Click(object sender, MouseButtonEventArgs e)
    {
        var files = SystemDialogs.SelectFiles(Lang.Text("Instance.Horizon.Packs.FileFilter"), Lang.Text("Instance.Horizon.Packs.Add"));
        if (files.Length > 0) _AddFiles(files);
    }

    /// <summary>添加资源包或光影包（默认关闭）。也供主窗口拖放 .hshader 时调用。</summary>
    public static void AddFilesToCurrentInstance(IEnumerable<string> files)
    {
        var gameDir = GameDir ?? InstanceList.McMcInstanceSelected?.PathIndie.TrimEnd('\\');
        if (gameDir is null) return;
        var added = new List<string>();
        foreach (var file in files)
        {
            try
            {
                added.Add(Path.GetFileName(HorizonPacks.AddPack(gameDir, file)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                HintService.Show(Hints.Error(ex.Message));
                break;
            }
        }

        if (added.Count > 0) HintService.Show(Hints.Success(Lang.Text("Instance.Horizon.Packs.Added", string.Join(", ", added))));
        Common.RunInUi(() => Launcher.frmInstanceHorizonPacks?.Reload());
    }

    private static void _AddFiles(IEnumerable<string> files) => AddFilesToCurrentInstance(files);

    private void BtnBrowse_Click(object sender, MouseButtonEventArgs e)
    {
        // 光影与资源包的浏览在模组页的浏览视图中
        Launcher.frmInstanceLeft?.PageChange(FormMain.PageSubType.VersionMod);
        Common.RunInUi(() => Launcher.frmInstanceHorizonMods?.OpenBrowser());
    }

    private void BtnOpenResource_Click(object sender, MouseButtonEventArgs e) => _OpenFolder(HorizonPacks.ResourcePacksDir);

    private void BtnOpenShader_Click(object sender, MouseButtonEventArgs e) => _OpenFolder(HorizonPacks.ShaderPacksDir);

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e) => Reload();

    private static void _OpenFolder(string name)
    {
        var gameDir = GameDir;
        if (gameDir is null) return;
        var path = Path.Combine(gameDir, name);
        Directory.CreateDirectory(path);
        Common.OpenExplorer(path);
    }

    #region 数据包

    private string? SelectedWorld => (ComboWorld.SelectedItem as MyComboBoxItem)?.Tag as string;

    private void ComboWorld_Changed(object sender, SelectionChangedEventArgs e) => _ReloadDataPacks();

    private void _ReloadDataPacks()
    {
        PanDataPacks.Children.Clear();
        var world = SelectedWorld;
        if (world is null)
        {
            LabDataPacksEmpty.Text = Lang.Text("Instance.Horizon.Packs.NoWorld");
            LabDataPacksEmpty.Visibility = Visibility.Visible;
            CardDataPacks.Title = Lang.Text("Instance.Horizon.Packs.DataPacks");
            return;
        }

        var packs = HorizonPacks.ListDataPacks(world);
        foreach (var pack in packs) PanDataPacks.Children.Add(_CreateRow(pack, world));
        LabDataPacksEmpty.Text = Lang.Text("Instance.Horizon.Packs.DataPacksEmpty");
        LabDataPacksEmpty.Visibility = packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardDataPacks.Title = Lang.Text("Instance.Horizon.Packs.DataPacksCount", packs.Count);
    }

    private void BtnAddDataPack_Click(object sender, MouseButtonEventArgs e)
    {
        var world = SelectedWorld;
        if (world is null)
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Packs.NoWorld")));
            return;
        }

        var files = SystemDialogs.SelectFiles(Lang.Text("Instance.Horizon.Packs.DataPackFilter"), Lang.Text("Instance.Horizon.Packs.AddDataPack"));
        var added = new List<string>();
        foreach (var file in files)
        {
            try
            {
                added.Add(Path.GetFileName(HorizonPacks.AddDataPack(world, file)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                HintService.Show(Hints.Error($"{Path.GetFileName(file)}：{ex.Message}"));
            }
        }

        if (added.Count > 0) HintService.Show(Hints.Success(Lang.Text("Instance.Horizon.Packs.DataPackAdded", string.Join(", ", added))));
        _ReloadDataPacks();
    }

    private void BtnOpenDataPacks_Click(object sender, MouseButtonEventArgs e)
    {
        var world = SelectedWorld;
        if (world is null) return;
        var dir = Path.Combine(world, "datapacks");
        Directory.CreateDirectory(dir);
        Common.OpenExplorer(dir);
    }

    #endregion

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
