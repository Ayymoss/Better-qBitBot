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

public sealed record GeminiResponse
{
    [JsonPropertyName("intent")]
    public string Intent { get; init; } = "off_topic";

    [JsonPropertyName("confidence")]
    public ConfidenceLevel Confidence { get; init; } = ConfidenceLevel.Low;

    [JsonPropertyName("response")]
    public string Response { get; init; } = string.Empty;

    [JsonPropertyName("resources")]
    public List<string> Resources { get; init; } = [];

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; init; } = string.Empty;

    [JsonPropertyName("follow_up_questions")]
    public List<string> FollowUpQuestions { get; init; } = [];

    // Populated post-deserialization from Gemini thinking parts (where part.thought == true).
    // Not part of the structured-output schema.
    [JsonIgnore]
    public string ThoughtSummary { get; init; } = string.Empty;

    public bool ShouldRespond => Intent is "on_topic";
    public bool IsPiracy => Intent is "piracy";
    public bool IsOffTopic => Intent is "off_topic";
}
