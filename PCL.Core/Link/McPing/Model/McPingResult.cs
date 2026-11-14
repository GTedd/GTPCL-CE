using System.Text.Json.Serialization;

namespace PCL.Core.Link.McPing.Model;

public record McPingResult(
    [property: JsonPropertyName("version")] McPingVersionResult Version,
    [property: JsonPropertyName("players")] McPingPlayerResult Players,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("favicon")] string? Favicon,
    [property: JsonPropertyName("latency")] long Latency,
    [property: JsonPropertyName("modinfo")] McPingModInfoResult? ModInfo,
    [property: JsonPropertyName("preventsChatReports")] bool? PreventsChatReports)
{
    /// <summary>结果来自哪种协议。</summary>
    [JsonIgnore]
    public McServerEdition Edition { get; init; } = McServerEdition.Java;
}
