namespace qBitBotNew.Config;

public sealed record GeminiConfig
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gemini-3.5-flash";
}
