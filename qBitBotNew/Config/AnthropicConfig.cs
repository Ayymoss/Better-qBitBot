namespace qBitBotNew.Config;

public sealed record AnthropicConfig
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-5-5";

    // low | medium | high | xhigh | max. Haiku 5.5 defaults to medium.
    public string Effort { get; init; } = "medium";

    // Web search costs $10 / 1k searches on top of tokens — by far the biggest line item at
    // this bot's prompt sizes. Disable to answer from model knowledge only.
    public bool WebSearchEnabled { get; init; } = true;
    public int WebSearchMaxUses { get; init; } = 3;
}
