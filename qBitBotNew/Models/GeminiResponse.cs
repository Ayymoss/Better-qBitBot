using System.Text.Json.Serialization;

namespace qBitBotNew.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ConfidenceLevel>))]
public enum ConfidenceLevel
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High
}

public sealed class GeminiResponse
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "off_topic";

    [JsonPropertyName("confidence")]
    public ConfidenceLevel Confidence { get; set; } = ConfidenceLevel.Low;

    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;

    [JsonPropertyName("resources")]
    public List<string> Resources { get; set; } = [];

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = string.Empty;

    public bool ShouldRespond => Intent is "on_topic";
    public bool IsPiracy => Intent is "piracy";
    public bool IsOffTopic => Intent is "off_topic";
}
