using System.Collections.Generic;
using System.Linq;
using System;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.UI.Hints;

namespace PCL;

/// <summary>把 <see cref="HintService" /> 的提示呈现到主窗体，只负责界面显示。</summary>
public sealed class HintPresenter : IHintPresenter
{
    public static HintPresenter Instance { get; } = new();

    /// <summary>注册到 Core 提示服务。</summary>
    public static void Install() => HintService.Instance.Presenter = Instance;

    public bool IsAvailable => Launcher.frmMain?.PanHint is not null;

    public void Present(HintViewModel hint)
    {
        var main = Launcher.frmMain;
        if (main?.PanHint is null) return;

        main.PanHint.HorizontalAlignment = HorizontalAlignment.Right;
        main.PanHint.VerticalAlignment = VerticalAlignment.Bottom;

        var extraHeight = main.PanExtraButtons.ActualHeight;
        main.PanHint.Margin = new Thickness(0, 0, 0, extraHeight > 0 ? extraHeight + 20 : 20);

        var toast = new MyToast(hint);
        // 延迟显示期间控件已经进了可视树，先把透明度压到 0，避免闪一帧完整样式再回缩。
        toast.Opacity = 0;
        main.PanHint.Children.Add(toast);

        // 构建 toast 模板、测量文本、挂动画都要占用 UI 线程。
        // 这里通常正好是下载刚结束、主界面忙于刷新任务条的时刻，同步做完会让界面明显顿一下，
        // 所以推迟到布局与渲染之后再做，让提示单独占一帧。
        main.Dispatcher.BeginInvoke(new Action(toast.Show), DispatcherPriority.Loaded);
    }

    public void Dismiss(HintViewModel hint)
    {
        var toast = FindToast(hint);
        toast?.Dismiss();
    }

    public void DismissAll()
    {
        var main = Launcher.frmMain;
        if (main?.PanHint is null) return;

        foreach (var toast in main.PanHint.Children.OfType<MyToast>().ToList())
            toast.Dismiss();
    }

    private static MyToast? FindToast(HintViewModel hint)
    {
        var main = Launcher.frmMain;
        if (main?.PanHint is null) return null;

        return main.PanHint.Children.OfType<MyToast>().FirstOrDefault(toast => ReferenceEquals(toast.Hint, hint));
    }
}
