using System.Text.Json.Serialization;

namespace qBitBotNew.Models;

public sealed class GeminiResponse
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "off_topic";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

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
