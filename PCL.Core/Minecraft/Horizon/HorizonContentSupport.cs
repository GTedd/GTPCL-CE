using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace PCL.Core.Minecraft.Horizon;

/// <summary>
///     一个 Horizon 构建能加载的内容，即 <c>horizon-client --launch-api</c> 的 <c>content</c>
///     （docs/api/content.md，“What a build loads”）。
/// </summary>
public sealed record HorizonContentSupport
{
    public static HorizonContentSupport Unknown { get; } = new();

    /// <summary>内容模组清单 <c>api</c> 可取的值（如 <c>0.2</c>、<c>0.3</c>）。</summary>
    public IReadOnlyList<string> ModApis { get; init; } = [];

    /// <summary>原生模组的 hooks 上限；为 null 表示构建不支持原生模组。</summary>
    public int? NativeHooks { get; init; }

    /// <summary>原生模组是否需要重新编译游戏（目前总是 true）。</summary>
    public bool NativeRebuild { get; init; }

    public IReadOnlyList<string> ShaderApis { get; init; } = [];
    public int? ShaderSchema { get; init; }

    /// <summary>资源包格式 <c>[major, minor]</c>。</summary>
    public (int Major, int Minor)? ResourcePackFormat { get; init; }

    public (int Major, int Minor)? DataPackFormat { get; init; }
    public int? ModpackFormat { get; init; }
    public int? IndexSchema { get; init; }

    public bool IsKnown => ModApis.Count > 0 || NativeHooks is not null;

    public static HorizonContentSupport Parse(JsonObject? content)
    {
        if (content is null) return Unknown;
        return new HorizonContentSupport
        {
            ModApis = _Strings(content["mod"]?["api"]),
            NativeHooks = _Int(content["native_mod"]?["hooks"]),
            NativeRebuild = content["native_mod"]?["rebuild"] is JsonValue r && r.TryGetValue<bool>(out var rebuild) && rebuild,
            ShaderApis = _Strings(content["shader"]?["api"]),
            ShaderSchema = _Int(content["shader"]?["schema"]),
            ResourcePackFormat = _Format(content["resourcepack"]?["format"]),
            DataPackFormat = _Format(content["datapack"]?["format"]),
            ModpackFormat = _Int(content["modpack"]?["format"]),
            IndexSchema = _Int(content["index"]?["schema"])
        };
    }

    /// <summary>内容模组能否在这个构建加载；构建信息未知时视为可以。</summary>
    public bool SupportsModApi(string? api) => ModApis.Count == 0 || api is not null && ModApis.Contains(api);

    /// <summary>原生模组的 hooks 要求是否满足；未声明 hooks 的模组总是满足。</summary>
    public bool SupportsNativeHooks(int? hooks) => hooks is null || NativeHooks is null || hooks <= NativeHooks;

    private static IReadOnlyList<string> _Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];

    private static int? _Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static (int, int)? _Format(JsonNode? node) =>
        node is JsonArray { Count: >= 1 } array && _Int(array[0]) is { } major
            ? (major, array.Count > 1 ? _Int(array[1]) ?? 0 : 0)
            : null;
}
