using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     Horizon 发布说明（GitHub release 正文，Markdown）的整理。
///     正文开头是给 GitHub 下载页看的 HTML 横幅，结尾是致谢名单与免责声明，条目后带 PR 与 issue 编号；
///     在启动器里只留版本信息与“New / Fixed / Known issues”等小节，与官方启动器首页的“What's new”取法相同。
/// </summary>
public static partial class HorizonChangelog
{
    [GeneratedRegex(@"\s*\((?:fixes\s+)?#\d+(?:,\s*(?:fixes\s+)?#\d+)*\)")]
    private static partial Regex IssueRefs();

    /// <summary>整理发布说明；没有内容时返回空字符串。</summary>
    public static string Clean(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            // 分隔线之后是免责声明；致谢名单很长，也不在启动器里显示
            if (trimmed.Length >= 3 && trimmed.Trim('-').Length == 0) break;
            if (trimmed.StartsWith("Thanks to ", StringComparison.Ordinal)) break;
            if (trimmed.StartsWith("### Contributors", StringComparison.OrdinalIgnoreCase)) break;
            // 开头的 HTML 横幅是给 GitHub 下载页看的（“不要直接运行下面的文件”）
            if (trimmed.StartsWith('<')) continue;
            line = IssueRefs().Replace(line, "");
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }
}
