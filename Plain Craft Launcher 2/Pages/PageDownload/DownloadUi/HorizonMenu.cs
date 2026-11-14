using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.Minecraft.Horizon;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;

namespace PCL;

public static partial class DownloadUi
{
    /// <summary>
    ///     Horizon 版本列表项：标题为版本名，副行标出“最新”“当前使用”、发布日期与摘要；
    ///     悬停时有“更新日志”按钮，右键也能直接查看更新日志。
    /// </summary>
    public static MyListItem HorizonListItem(HorizonRemoteBuild build, bool isLatest, bool isCurrent)
    {
        var info = new List<string>();
        if (isLatest) info.Add(Lang.Text("Download.Install.Horizon.Latest"));
        if (isCurrent) info.Add(Lang.Text("Download.Install.Horizon.Current"));
        if (build.IsNightly)
        {
            info.Add(Lang.Text(build.Tag == HorizonRelease.NightlyTag
                ? "Download.Install.Horizon.NightlyRolling"
                : "Download.Install.Horizon.NightlyArchived"));
        }
        else
        {
            if (build.PublishedAt is { } published) info.Add(published.ToLocalTime().ToString("yyyy-MM-dd"));
            if (build.Summary.Length > 0) info.Add(build.Summary);
        }

        var item = new MyListItem
        {
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = build,
            Logo = $"{Common.PathImage}Blocks/CommandBlock.png",
            Title = build.DisplayName,
            Info = string.Join(" · ", info),
            ContentHandler = (sender, _) =>
            {
                var btnInfo = CreateChangelogButton();
                ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
                ToolTipService.SetVerticalOffset(btnInfo, 30d);
                ToolTipService.SetHorizontalOffset(btnInfo, 2d);
                btnInfo.Click += (_, _) => HorizonChangelogShow(build);
                sender.Buttons = new[] { btnInfo };
            }
        };
        item.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            HorizonChangelogShow(build);
        };
        return item;
    }

    /// <summary>
    ///     显示 Horizon 版本的更新日志（GitHub 发布说明，去掉下载页横幅、PR 编号与致谢名单）；中文界面下可机器翻译。
    /// </summary>
    public static void HorizonChangelogShow(HorizonRemoteBuild build)
    {
        Common.RunInNewThread(() =>
        {
            var changelog = HorizonChangelog.Clean(build.Changelog);
            var target = _HorizonTranslateTarget();
            var canTranslate = target is not null && changelog.Length > 0;
            var choice = DialogService.Instance.Show(new MarkdownDialogRequest
            {
                Title = Lang.Text("Download.Install.Horizon.Changelog.Title", build.DisplayName),
                Content = changelog.Length == 0 ? Lang.Text("Download.Install.Horizon.Changelog.Empty") : changelog,
                Buttons = canTranslate
                    ? DialogButtons.Create("Common.Action.Confirm", "Download.Install.Horizon.Changelog.Translate",
                        "Download.Install.Horizon.Changelog.OpenGitHub")
                    : DialogButtons.Create("Common.Action.Confirm", "Download.Install.Horizon.Changelog.OpenGitHub"),
                ForceWait = true
            }).Result?.Button ?? 0;
            if (canTranslate && choice == 2)
                _HorizonChangelogTranslate(build, changelog, target!);
            else if (choice == (canTranslate ? 3 : 2))
                Common.OpenWebsite(build.ReleaseUrl);
        }, "Horizon Changelog");
    }

    /// <summary>界面语言为中文时的翻译目标（简体或繁体），否则为 null。</summary>
    private static string? _HorizonTranslateTarget()
    {
        var name = Lang.Culture.Name;
        if (!name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return null;
        return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name is "zh-TW" or "zh-HK" or "zh-MO"
            ? TextTranslator.TraditionalChinese
            : TextTranslator.SimplifiedChinese;
    }

    private static string _HorizonTranslationCachePath(string changelog, string target)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target + "\n" + changelog)))[..32];
        return Path.Combine(Common.pathTemp, "Cache", "HorizonChangelog", $"{hash}.md");
    }

    private static void _HorizonChangelogTranslate(HorizonRemoteBuild build, string changelog, string target)
    {
        string translated;
        var cachePath = _HorizonTranslationCachePath(changelog, target);
        try
        {
            if (File.Exists(cachePath))
            {
                translated = File.ReadAllText(cachePath, Encoding.UTF8);
            }
            else
            {
                var total = TextTranslator.Split(changelog).Count;
                HintService.Show(Hints.Info(Lang.Text("Download.Install.Horizon.Changelog.Translating", build.DisplayName, total)));
                var progress = new _QuarterProgress((done, all) => HintService.Show(Hints.Info(
                    Lang.Text("Download.Install.Horizon.Changelog.TranslatingProgress", build.DisplayName, done, all))));
                translated = TextTranslator.Default.TranslateAsync(changelog, target, total > 1 ? progress : null)
                    .GetAwaiter().GetResult();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllText(cachePath, translated, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Common.Log(ex, "写入 Horizon 更新日志翻译缓存失败");
                }
            }
        }
        catch (Exception ex)
        {
            Common.Log(ex, "翻译 Horizon 更新日志失败", Common.LogLevel.Hint,
                userSummary: Lang.Text("Download.Install.Horizon.Changelog.TranslateFailed"));
            return;
        }

        var choice = DialogService.Instance.Show(new MarkdownDialogRequest
        {
            Title = Lang.Text("Download.Install.Horizon.Changelog.TranslatedTitle", build.DisplayName),
            Content = translated,
            Buttons = DialogButtons.Create("Common.Action.Confirm", "Download.Install.Horizon.Changelog.ShowOriginal"),
            ForceWait = true
        }).Result?.Button ?? 0;
        if (choice == 2) HorizonChangelogShow(build);
    }

    /// <summary>翻译进度：每完成约四分之一提示一次，避免提示刷屏。</summary>
    private sealed class _QuarterProgress(Action<int, int> report) : IProgress<(int Done, int Total)>
    {
        private readonly object _lock = new();
        private int _lastQuarter;

        public void Report((int Done, int Total) value)
        {
            if (value.Done >= value.Total) return; // 完成后直接弹出译文
            var quarter = value.Done * 4 / Math.Max(1, value.Total);
            lock (_lock)
            {
                if (quarter <= _lastQuarter) return;
                _lastQuarter = quarter;
            }

            report(value.Done, value.Total);
        }
    }
}
