using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Horizon;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

/// <summary>模组页的“原生构建”卡片：状态、构建按钮、编译配置与构建日志。</summary>
public partial class PageInstanceHorizonMods
{
    private int _nativeReloadVersion;

    /// <summary>重新读取原生构建状态并刷新卡片。</summary>
    public void ReloadNativeBuild()
    {
        var instance = Instance;
        if (instance is null || !HorizonInstance.IsHorizon(instance)) return;
        var version = Interlocked.Increment(ref _nativeReloadVersion);
        Common.RunInNewThread(() =>
        {
            HorizonNativeStatus status;
            try
            {
                status = HorizonNativeBuild.GetStatus(instance);
            }
            catch (Exception ex)
            {
                Common.Log(ex, "[Horizon] 读取原生构建状态失败");
                return;
            }

            Common.RunInUi(() =>
            {
                if (version == _nativeReloadVersion) _RenderNativeBuild(instance, status);
            });
        }, "Horizon Native Status");
    }

    private void _RenderNativeBuild(McInstance instance, HorizonNativeStatus status)
    {
        PanNativeBuild.Children.Clear();
        var building = HorizonNativeBuild.IsBuilding;

        var text = building
            ? Lang.Text("Instance.Horizon.Native.Building")
            : status.State switch
            {
                HorizonNativeState.NoMods => Lang.Text("Instance.Horizon.Native.NoMods"),
                HorizonNativeState.NotBuilt => Lang.Text("Instance.Horizon.Native.NotBuilt", status.Enabled.Count),
                HorizonNativeState.NeedsRebuild => Lang.Text("Instance.Horizon.Native.NeedsRebuild", status.PlayedVersion ?? "-"),
                _ => Lang.Text("Instance.Horizon.Native.UpToDate", status.PlayedVersion ?? "-", status.Record?.Base ?? "-",
                    status.Record?.Profile ?? "-")
            };
        var lines = new List<string> { text };
        if (status.State == HorizonNativeState.UpToDate && status.Record is { LeftOut.Count: > 0 } record)
            lines.Add(Lang.Text("Instance.Horizon.Native.LeftOut",
                string.Join("、", record.LeftOut.Select(l => $"{l.Id} {l.Version}（{_LeftOutReason(l.Why)}）"))));
        lines.AddRange(status.Problems.Select(p => Lang.Text("Instance.Horizon.Mods.Unreadable", p)));

        PanNativeBuild.Children.Add(new TextBlock
        {
            Text = string.Join("\n", lines),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = status.State is HorizonNativeState.NotBuilt or HorizonNativeState.NeedsRebuild && !building
                ? (System.Windows.Media.Brush)FindResource("ColorBrush3")
                : (System.Windows.Media.Brush)FindResource("ColorBrushGray3")
        });

        var buttons = new WrapPanel();
        var canBuild = !building && status.State != HorizonNativeState.NoMods;
        var btnBuild = new MyButton
        {
            Text = Lang.Text(status.State == HorizonNativeState.UpToDate ? "Instance.Horizon.Native.Rebuild" : "Instance.Horizon.Native.Build"),
            MinWidth = 130, Height = 35, Padding = new Thickness(13, 0, 13, 0), Margin = new Thickness(0, 0, 12, 8),
            ColorType = status.State is HorizonNativeState.NotBuilt or HorizonNativeState.NeedsRebuild
                ? MyButton.ColorState.Highlight
                : MyButton.ColorState.Normal,
            IsEnabled = canBuild
        };
        btnBuild.Click += (_, _) => _StartNativeBuild(instance, status);
        buttons.Children.Add(btnBuild);

        var profile = new MyComboBox
        {
            Width = 150, Height = 35, Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Lang.Text("Instance.Horizon.Native.ProfileTip"), IsEnabled = !building
        };
        foreach (var (key, value) in new[]
                 {
                     ("Instance.Horizon.Native.Profile.Performance", HorizonNativeProfile.Performance),
                     ("Instance.Horizon.Native.Profile.Dev", HorizonNativeProfile.Dev),
                     ("Instance.Horizon.Native.Profile.Release", HorizonNativeProfile.Release)
                 })
            profile.Items.Add(new MyComboBoxItem { Content = Lang.Text(key), Tag = value });
        profile.SelectedIndex = Math.Clamp(States.Tool.HorizonNativeProfile, 0, 2);
        profile.SelectionChanged += (_, _) =>
        {
            if (profile.SelectedIndex >= 0) States.Tool.HorizonNativeProfile = profile.SelectedIndex;
        };
        buttons.Children.Add(profile);

        var btnLog = new MyButton
        {
            Text = Lang.Text("Instance.Horizon.Native.Log"), MinWidth = 110, Height = 35,
            Padding = new Thickness(13, 0, 13, 0), Margin = new Thickness(0, 0, 12, 8)
        };
        btnLog.Click += (_, _) =>
        {
            var log = HorizonNativeBuild.LogPath(instance);
            if (File.Exists(log)) Common.OpenExplorer(log);
            else HintService.Show(Hints.Info(Lang.Text("Instance.Horizon.Native.NoLog")));
        };
        buttons.Children.Add(btnLog);
        PanNativeBuild.Children.Add(buttons);
    }

    private static string _LeftOutReason(string why) => why switch
    {
        "clash" => Lang.Text("Instance.Horizon.Native.Why.Clash"),
        "needs" => Lang.Text("Instance.Horizon.Native.Why.Needs"),
        "unsupported" => Lang.Text("Instance.Horizon.Native.Why.Unsupported"),
        _ => Lang.Text("Instance.Horizon.Native.Why.OtherVersion")
    };

    private void _StartNativeBuild(McInstance instance, HorizonNativeStatus status)
    {
        if (_IsGameRunning())
        {
            HintService.Show(Hints.Error(Lang.Text("Instance.Horizon.Mods.GameRunning")));
            return;
        }

        // 每次构建前都提醒：原生模组以玩家的权限运行，不在沙盒里
        var mods = string.Join("\n", status.Enabled.Select(m => $"· {m.Id} {m.Version}"));
        var buildRoot = HorizonNativeBuild.BuildRoot(instance);
        var firstTime = !HorizonRustToolchain.IsInstalled(buildRoot);
        var content = Lang.Text("Instance.Horizon.Native.ConfirmMessage", mods) +
                      (firstTime ? "\n\n" + Lang.Text("Instance.Horizon.Native.FirstBuild", buildRoot) : "");
        if ((DialogService.Instance.Show(new TextDialogRequest
            {
                Title = Lang.Text("Instance.Horizon.Native.ConfirmTitle"),
                Content = content,
                Buttons = DialogButtons.Create("Instance.Horizon.Native.ConfirmBuild", "Common.Action.Cancel"),
                IsWarn = true,
                ForceWait = true
            }).Result?.Button ?? 0) != 1)
            return;

        try
        {
            var loader = HorizonNativeBuild.StartBuild(instance, (HorizonNativeProfile)Math.Clamp(States.Tool.HorizonNativeProfile, 0, 2),
                state =>
                {
                    switch (state.State)
                    {
                        case LoadState.Finished:
                            HintService.Show(Hints.Success(Lang.Text("Instance.Horizon.Native.Done", instance.Name)));
                            break;
                        case LoadState.Failed:
                            Common.Log(state.Error, Lang.Text("Instance.Horizon.Native.Failed"), Common.LogLevel.Msgbox,
                                userSummary: state.Error?.Message);
                            break;
                    }

                    if (state.State is LoadState.Finished or LoadState.Failed or LoadState.Aborted)
                        Common.RunInUi(() => Launcher.frmInstanceHorizonMods?.Reload());
                });
            Loading.LoaderTaskbarAdd(loader);
            Launcher.frmMain!.BtnExtraDownload.ShowRefresh();
            Launcher.frmMain.BtnExtraDownload.Ribble();
            HintService.Show(Hints.Info(Lang.Text("Instance.Horizon.Native.Started")));
        }
        catch (InvalidOperationException ex)
        {
            HintService.Show(Hints.Error(ex.Message));
        }

        ReloadNativeBuild();
    }
}
