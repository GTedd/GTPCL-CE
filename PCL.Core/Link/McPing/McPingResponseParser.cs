using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCL.Core.App.Localization;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Utils;

namespace PCL.Core.Link.McPing;

/// <summary>
/// 解析 Java 版 Server List Ping 返回的状态 JSON，并把聊天组件格式的 MOTD 转为启动器 MOTD 渲染器使用的
/// § 格式代码字符串（十六进制颜色以 <c>#RRGGBB</c> 表示）。
/// </summary>
/// <remarks>
/// 字段缺失或类型不符时尽量给出默认值而不是整体失败：不少代理、插件和面板会省略 <c>players</c>、
/// <c>version</c>，或把数字写成字符串。
/// </remarks>
public static partial class McPingResponseParser
{
    private const int MaxComponentDepth = 64;

    private static readonly Dictionary<string, char> _NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = '0',
        ["dark_blue"] = '1',
        ["dark_green"] = '2',
        ["dark_aqua"] = '3',
        ["dark_red"] = '4',
        ["dark_purple"] = '5',
        ["gold"] = '6',
        ["gray"] = '7',
        ["dark_gray"] = '8',
        ["blue"] = '9',
        ["green"] = 'a',
        ["aqua"] = 'b',
        ["red"] = 'c',
        ["light_purple"] = 'd',
        ["yellow"] = 'e',
        ["white"] = 'f'
    };

    /// <summary>
    /// 解析状态 JSON。
    /// </summary>
    /// <exception cref="InvalidDataException">内容不是 JSON 对象，或不含任何服务器状态字段。</exception>
    public static McPingResult Parse(string json)
    {
        if (_ParseRoot(json) is not JsonObject root)
            throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));

        var hasVersion = root.TryGetPropertyValue("version", out var versionNode);
        var hasPlayers = root.TryGetPropertyValue("players", out var playersNode);
        var hasDescription = root.TryGetPropertyValue("description", out var descriptionNode);
        if (!hasVersion && !hasPlayers && !hasDescription)
            throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"));

        return new McPingResult(
            _ParseVersion(versionNode),
            _ParsePlayers(playersNode),
            MotdToLegacyText(descriptionNode),
            _GetString(root["favicon"]),
            0,
            _ParseModInfo(root),
            _GetBool(root["preventsChatReports"]));
    }

    /// <summary>
    /// 把 MOTD（字符串、聊天组件对象或组件数组）转为 § 格式代码字符串。子组件按原版规则继承父组件样式。
    /// </summary>
    public static string MotdToLegacyText(JsonNode? node)
    {
        var writer = new LegacyTextWriter();
        _AppendComponent(node, TextStyle.Empty, writer, 0);
        return NormalizeLegacyText(writer.ToString());
    }

    /// <summary>
    /// 把 BungeeCord 风格的十六进制颜色（<c>§x§R§R§G§G§B§B</c>）转为渲染器识别的 <c>#RRGGBB</c>。
    /// </summary>
    public static string NormalizeLegacyText(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('§')) return text;
        return _BungeeHexColor().Replace(text, match =>
        {
            var hex = new StringBuilder("#", 7);
            foreach (Capture digit in match.Groups["digit"].Captures) hex.Append(char.ToUpperInvariant(digit.Value[0]));
            return hex.ToString();
        });
    }

    /// <summary>去掉 § 格式代码与 <c>#RRGGBB</c> 颜色标记，得到纯文本。</summary>
    public static string StripFormatting(string text) =>
        string.IsNullOrEmpty(text) ? text : RegexPatterns.MotdCode.Replace(_AnyFormatCode().Replace(text, ""), "");

    private static JsonNode? _ParseRoot(string json)
    {
        try
        {
            return JsonCompat.ParseNode(json);
        }
        catch (ArgumentException)
        {
            // 大小写不敏感解析遇到仅大小写不同的重复键时会抛出，退回大小写敏感解析
            try
            {
                return JsonNode.Parse(json, documentOptions: JsonCompat.DocumentOptions);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"), ex);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Lang.Text("Tools.ServerQuery.Error.InvalidResponse"), ex);
        }
    }

    private static McPingVersionResult _ParseVersion(JsonNode? node) => node switch
    {
        JsonObject obj => new McPingVersionResult(_GetString(obj["name"]) ?? string.Empty, _GetInt(obj["protocol"]) ?? -1),
        JsonValue value => new McPingVersionResult(_ValueToString(value), -1),
        _ => new McPingVersionResult(string.Empty, -1)
    };

    private static McPingPlayerResult _ParsePlayers(JsonNode? node)
    {
        if (node is not JsonObject obj) return new McPingPlayerResult(0, 0, null);

        List<McPingPlayerSampleResult>? samples = null;
        if (obj["sample"] is JsonArray sampleArray)
        {
            samples = [];
            foreach (var item in sampleArray)
            {
                if (item is not JsonObject player) continue;
                var name = _GetString(player["name"]);
                if (name is null) continue;
                samples.Add(new McPingPlayerSampleResult(name, _GetString(player["id"]) ?? string.Empty));
            }
        }

        return new McPingPlayerResult(_GetInt(obj["max"]) ?? 0, _GetInt(obj["online"]) ?? 0, samples);
    }

    private static McPingModInfoResult? _ParseModInfo(JsonObject root)
    {
        // Forge 1.7 - 1.12
        if (root["modinfo"] is JsonObject modInfo)
        {
            List<McPingModInfoModResult> mods = [];
            if (modInfo["modList"] is JsonArray modList)
            {
                foreach (var item in modList)
                {
                    if (item is not JsonObject mod || _GetString(mod["modid"]) is not { } id) continue;
                    mods.Add(new McPingModInfoModResult(id, _GetString(mod["version"]) ?? string.Empty));
                }
            }

            return new McPingModInfoResult(_GetString(modInfo["type"]) ?? "FML", mods);
        }

        // Forge 1.13+ / NeoForge
        if (root["forgeData"] is JsonObject forgeData)
        {
            List<McPingModInfoModResult> mods = [];
            if (forgeData["mods"] is JsonArray modList)
            {
                foreach (var item in modList)
                {
                    if (item is not JsonObject mod || _GetString(mod["modId"]) is not { } id) continue;
                    mods.Add(new McPingModInfoModResult(id, _GetString(mod["modmarker"]) ?? string.Empty));
                }
            }

            var networkVersion = _GetInt(forgeData["fmlNetworkVersion"]);
            return new McPingModInfoResult(networkVersion is { } v ? $"FML{v}" : "FML", mods);
        }

        return null;
    }

    private static void _AppendComponent(JsonNode? node, TextStyle parent, LegacyTextWriter writer, int depth)
    {
        if (node is null || depth > MaxComponentDepth) return;

        switch (node)
        {
            case JsonValue value:
                writer.Append(parent, _ValueToString(value));
                break;

            case JsonArray array:
            {
                if (array.Count == 0) return;
                // 原版把组件数组视为“第一个组件 + 其余组件作为它的兄弟”，其余组件继承第一个组件的样式
                var first = array[0];
                _AppendComponent(first, parent, writer, depth + 1);
                var siblingStyle = first is JsonObject firstObj ? parent.Merge(firstObj) : parent;
                for (var i = 1; i < array.Count; i++)
                    _AppendComponent(array[i], siblingStyle, writer, depth + 1);
                break;
            }

            case JsonObject obj:
            {
                var style = parent.Merge(obj);
                writer.Append(style, _GetComponentText(obj, depth));
                if (obj["extra"] is JsonArray extra)
                    foreach (var child in extra)
                        _AppendComponent(child, style, writer, depth + 1);
                break;
            }
        }
    }

    private static string _GetComponentText(JsonObject obj, int depth)
    {
        if (obj["text"] is JsonValue text) return _ValueToString(text);

        if (_GetString(obj["translate"]) is { } key)
        {
            if (_GetString(obj["fallback"]) is { } fallback) key = fallback;
            if (obj["with"] is not JsonArray args || args.Count == 0) return key;

            // 参数按纯文本代入，避免参数里的格式代码打乱外层样式
            var plainArgs = new List<string>(args.Count);
            foreach (var arg in args)
            {
                var writer = new LegacyTextWriter();
                _AppendComponent(arg, TextStyle.Empty, writer, depth + 1);
                plainArgs.Add(StripFormatting(writer.ToString()));
            }

            return _FormatTranslation(key, plainArgs);
        }

        return _GetString(obj["keybind"]) ?? string.Empty;
    }

    private static string _FormatTranslation(string format, IReadOnlyList<string> args)
    {
        var sequentialIndex = 0;
        return _TranslationPlaceholder().Replace(format, match =>
        {
            if (match.Value == "%%") return "%";
            var index = match.Groups["index"].Success
                ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) - 1
                : sequentialIndex++;
            return index >= 0 && index < args.Count ? args[index] : string.Empty;
        });
    }

    private static string _ValueToString(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s)) return s;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToJsonString(),
            _ => string.Empty
        };
    }

    private static string? _GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static bool? _GetBool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        return value.TryGetValue<string>(out var s) && bool.TryParse(s, out b) ? b : null;
    }

    private static int? _GetInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (value.TryGetValue<double>(out var d) && double.IsFinite(d))
            return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        if (value.TryGetValue<string>(out var s) &&
            long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        return null;
    }

    private static string? _ReadColor(JsonNode? node, string? inherited)
    {
        var color = _GetString(node)?.Trim();
        if (string.IsNullOrEmpty(color)) return inherited;
        if (color.Equals("reset", StringComparison.OrdinalIgnoreCase)) return null;
        if (_NamedColors.ContainsKey(color)) return color.ToLowerInvariant();
        if (RegexPatterns.HexColor.IsMatch(color)) return color.ToUpperInvariant();
        return inherited;
    }

    private static bool _ReadFlag(JsonNode? node, bool inherited) => _GetBool(node) ?? inherited;

    /// <summary>组件的有效样式（已合并父组件样式）。</summary>
    private readonly record struct TextStyle(
        string? Color,
        bool Bold,
        bool Italic,
        bool Underlined,
        bool Strikethrough,
        bool Obfuscated)
    {
        public static TextStyle Empty => default;

        public TextStyle Merge(JsonObject obj) => new(
            _ReadColor(obj["color"], Color),
            _ReadFlag(obj["bold"], Bold),
            _ReadFlag(obj["italic"], Italic),
            _ReadFlag(obj["underlined"] ?? obj["underline"], Underlined),
            _ReadFlag(obj["strikethrough"], Strikethrough),
            _ReadFlag(obj["obfuscated"], Obfuscated));

        public string ToCodes()
        {
            var sb = new StringBuilder();
            // 颜色代码会重置格式，所以颜色必须写在格式代码前面
            if (Color is null) sb.Append("§r");
            else if (_NamedColors.TryGetValue(Color, out var code)) sb.Append('§').Append(code);
            else sb.Append(Color);
            if (Bold) sb.Append("§l");
            if (Italic) sb.Append("§o");
            if (Underlined) sb.Append("§n");
            if (Strikethrough) sb.Append("§m");
            // §k 会让渲染器不停刷新随机字符，服务器列表里暂不启用
            return sb.ToString();
        }
    }

    /// <summary>按需输出样式代码：只有样式变化时才写入，减少冗余代码。</summary>
    private sealed class LegacyTextWriter
    {
        private readonly StringBuilder _sb = new();

        // 渲染器的初始状态等价于空样式；null 表示文本自带了 § 代码，当前状态未知
        private TextStyle? _current = TextStyle.Empty;

        public void Append(TextStyle style, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_current != style) _sb.Append(style.ToCodes());
            _sb.Append(text);
            _current = text.Contains('§') ? null : style;
        }

        public override string ToString() => _sb.ToString();
    }

    [GeneratedRegex("§[xX](?:§(?<digit>[0-9a-fA-F])){6}")]
    private static partial Regex _BungeeHexColor();

    [GeneratedRegex("§[0-9a-zA-Z]")]
    private static partial Regex _AnyFormatCode();

    [GeneratedRegex(@"%(?:(?<index>\d+)\$)?s|%%")]
    private static partial Regex _TranslationPlaceholder();
}
