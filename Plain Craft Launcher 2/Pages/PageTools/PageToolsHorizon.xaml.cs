using System.Windows;
using System.Windows.Input;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     工具 → Horizon：安装、更新并启动 Horizon。流程在 <see cref="ModHorizon" /> 中。
/// </summary>
public partial class PageToolsHorizon : IPageView
{
    public PageToolsHorizon()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshStatus();
    }

    public void OnNavigatedTo(object? parameter)
    {
        TextProxy.Text = Config.Tool.HorizonGithubProxy ?? "";
        RefreshStatus();
    }

    public void OnNavigatedFrom()
    {
    }

    /// <summary>刷新状态文本与按钮可用性，可从任意线程调用。</summary>
    public void RefreshStatus()
    {
        Common.RunInUi(() =>
        {
            var installed = ModHorizon.InstalledVersion;
            var running = ModHorizon.IsRunning;
            TextStatus.Text = running
                ? "正在准备或启动 Horizon，进度见右上角的任务列表。"
                : installed is null
                    ? "尚未安装 Horizon。点击“启动 Horizon”会下载最新构建，并从本机的原版 26.3 准备资源（首次需要几分钟）。"
                    : $"已安装：{installed}";
            BtnLaunch.IsEnabled = !running;
            BtnUpdate.IsEnabled = !running;
        });
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        _SaveProxy();
        ModHorizon.Start(false);
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        _SaveProxy();
        ModHorizon.Start(true);
    }

    private void BtnFolder_Click(object sender, MouseButtonEventArgs e)
    {
        ModHorizon.OpenFolder();
    }

    private void TextProxy_LostFocus(object sender, RoutedEventArgs e)
    {
        _SaveProxy();
    }

    private void _SaveProxy()
    {
        var text = TextProxy.Text.Trim();
        if (text != (Config.Tool.HorizonGithubProxy ?? "")) Config.Tool.HorizonGithubProxy = text;
    }
}
